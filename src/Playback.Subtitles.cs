// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.Collections.Concurrent;

namespace LocalMedia;

public sealed partial class Playback
{
    sealed record PreparedSubtitle(string Source, string Path, IDisposable Lease);
    sealed record SubtitleBatch(long Generation, long? Entry, Media Media, List<PreparedSubtitle> Files, List<Exception> Errors, bool Select, bool Remember);
    readonly ConcurrentQueue<SubtitleBatch> subtitleResults = new();
    readonly object subtitleResultGate = new();
    static readonly SemaphoreSlim subtitleWorkers = new(2, 2);
    CancellationTokenSource? subtitleCancel;
    long subtitleGeneration;

    void CancelSubtitles()
    {
        subtitleGeneration++;
        subtitleCancel?.Cancel(); subtitleCancel?.Dispose(); subtitleCancel = null;
        lock (subtitleResultGate)
            while (subtitleResults.TryDequeue(out var result)) foreach (var file in result.Files) file.Lease.Dispose();
    }

    void ScheduleSubtitles() => PrepareSubtitles(null, false, false);
    public void QueueSubtitle(string path, bool select = true, bool remember = true) => PrepareSubtitles(path, select, remember);

    void PrepareSubtitles(string? path, bool select, bool remember)
    {
        CancelSubtitles();
        if (Current is not Media owner || path == null && owner.Kind != "video") return;
        var source = new Media { Path = currentSource?.Path ?? owner.Path, Kind = owner.Kind, SubCodepage = owner.SubCodepage, ExternalSubtitles = owner.ExternalSubtitles.ToList() };
        long generation = subtitleGeneration;
        long? entry = currentEntry;
        var cancellation = subtitleCancel = new CancellationTokenSource();
        CancellationToken token = cancellation.Token;
        _ = Task.Run(async () =>
        {
            var files = new List<PreparedSubtitle>(); var errors = new List<Exception>(); bool entered = false;
            try
            {
                await subtitleWorkers.WaitAsync(token).ConfigureAwait(false); entered = true;
                var paths = path == null ? Subtitles.Discover(source) : [path];
                foreach (string candidate in paths.Take(128))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        string prepared = Subtitles.Prepare(candidate, source.SubCodepage, Path.Combine(store.DirectoryPath, "cache"), out var lease);
                        files.Add(new PreparedSubtitle(candidate, prepared, lease));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Text.DecoderFallbackException) { errors.Add(e); }
                }
                token.ThrowIfCancellationRequested();
                lock (subtitleResultGate)
                {
                    if (!token.IsCancellationRequested)
                    {
                        subtitleResults.Enqueue(new SubtitleBatch(generation, entry, owner, files, errors, select, remember));
                        files = [];
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { if (!token.IsCancellationRequested) subtitleResults.Enqueue(new SubtitleBatch(generation, entry, owner, [], [e], select, remember)); }
            finally { foreach (var file in files) file.Lease.Dispose(); if (entered) subtitleWorkers.Release(); }
        });
    }

    void CompleteSubtitles()
    {
        while (subtitleResults.TryDequeue(out var result))
        {
            bool Live() => result.Generation == subtitleGeneration && ReferenceEquals(Current, result.Media)
                && currentEntry == result.Entry && TryNativeEntry(out long entry) && entry == result.Entry;
            try
            {
                if (!Live()) continue;
                foreach (var error in result.Errors)
                {
                    Diagnostics.Log(store.DirectoryPath, error);
                    Failed?.Invoke("外挂字幕无法读取，请检查文件或选择字幕编码。");
                }
                foreach (var file in result.Files)
                {
                    if (!Live()) break;
                    try
                    {
                        Engine.Command("sub-add", file.Path, result.Select ? "select" : "auto", Path.GetFileName(file.Source), Subtitles.Language(currentSource?.Path ?? result.Media.Path, file.Source));
                        subtitleLeases.Add(file.Lease);
                        if (result.Select)
                        {
                            TrackChanged();
                            if (result.Remember)
                            {
                                if (!result.Media.ExternalSubtitles.Contains(file.Source, StringComparer.OrdinalIgnoreCase)) result.Media.ExternalSubtitles.Add(file.Source);
                                result.Media.Subtitle = Engine.Get("sid"); store.SaveMetadata(result.Media);
                            }
                        }
                    }
                    catch (Exception e) { Diagnostics.Log(store.DirectoryPath, e); Failed?.Invoke("外挂字幕加载失败，请检查字幕文件。"); }
                }
                if (Live() && !result.Select)
                {
                    if (result.Media.Subtitle == "auto") ChoosePreferredSubtitle();
                    else Safe(() => Engine.Set("sid", result.Media.Subtitle));
                    if (result.Files.Count > 0) TrackChanged();
                }
            }
            finally
            {
                foreach (var file in result.Files) if (!subtitleLeases.Contains(file.Lease)) file.Lease.Dispose();
            }
        }
    }
}
