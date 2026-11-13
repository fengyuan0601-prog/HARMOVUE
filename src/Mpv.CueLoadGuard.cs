// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace LocalMedia;

public sealed partial class Mpv
{
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern nint mpv_create_client(nint handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern void mpv_destroy(nint handle);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_hook_add(nint handle, ulong user, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int priority);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_hook_continue(nint handle, ulong id);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern void mpv_wakeup(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    struct HookEvent { public nint Name; public ulong Id; }

    // An occurrence has a private, unopenable URL until the native on_load
    // hook verifies its CUE. Ordinary entries still go straight to libmpv.
    // A separate client services the hook independently of the UI timer.
    internal sealed class CueLoadGuard : IDisposable
    {
        sealed record CueSource(Media Media, uint Track, string Path, string DeclaredPath, string Sheet, double Start, double? End);
        readonly ConcurrentDictionary<string, CueSource> paths = new(StringComparer.Ordinal);
        readonly ConcurrentDictionary<long, int> skipped = new();
        static readonly SemaphoreSlim probeSlots = new(2, 2);
        readonly string prefix = "harmovue-cue://" + Guid.NewGuid().ToString("N") + "/";
        readonly CancellationTokenSource closing = new();
        readonly Store store;
        readonly nint client;
        readonly Thread pump;
        long serial;

        internal CueLoadGuard(Mpv engine, Store store)
        {
            this.store = store;
            client = mpv_create_client(engine.Handle, "harmovue_cue");
            if (client == 0) throw new InvalidOperationException("无法初始化 CUE 播放检查。");
            try
            {
                if (mpv_hook_add(client, 0, "on_load", 0) < 0)
                    throw new InvalidOperationException("无法注册 CUE 播放检查。");
                pump = new Thread(Run) { IsBackground = true, Name = "HARMOVUE CUE" };
                pump.Start();
            }
            catch { mpv_destroy(client); closing.Dispose(); throw; }
        }

        internal string Add(Media media, string path, string sheet, double start, double? end, string? declaredPath = null)
        {
            string alias = prefix + (++serial).ToString(CultureInfo.InvariantCulture);
            paths[alias] = new CueSource(media, media.Track, path, declaredPath ?? path, sheet, start, end);
            return alias;
        }

        internal void ClearPaths() => paths.Clear();
        internal void Remove(string alias) => paths.TryRemove(alias, out _);

        internal bool ConsumeSkip(long entry)
        {
            while (skipped.TryGetValue(entry, out int count))
            {
                bool consumed = count == 1
                    ? skipped.TryRemove(new KeyValuePair<long, int>(entry, count))
                    : skipped.TryUpdate(entry, count - 1, count);
                if (consumed) return true;
            }
            return false;
        }

        void Run()
        {
            long? entry = null;
            while (!closing.IsCancellationRequested)
            {
                var message = Marshal.PtrToStructure<Event>(mpv_wait_event(client, -1));
                if (message.Id == 1) return; // MPV_EVENT_SHUTDOWN
                if (message.Id == 6) // MPV_EVENT_START_FILE precedes on_load.
                    entry = message.Data == 0 ? null : Marshal.ReadInt64(message.Data);
                if (message.Id != 25) continue; // MPV_EVENT_HOOK
                ulong hook = Marshal.PtrToStructure<HookEvent>(message.Data).Id;
                try { Resolve(entry); }
                catch (Exception e) { Debug.WriteLine(e); }
                finally { mpv_hook_continue(client, hook); }
            }
        }

        bool Aborted() => closing.IsCancellationRequested || ReadProperty(client, "playback-abort") == "yes";

        void Resolve(long? entry)
        {
            string alias = ReadProperty(client, "stream-open-filename");
            if (!paths.TryGetValue(alias, out var cue) || Aborted()) return;

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(12));
            CancellationToken token = deadline.Token;
            var probe = Task.Run(async () =>
            {
                await probeSlots.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    // Recheck the indexed record after disk I/O in case a scan
                    // or relink changed this occurrence while we were waiting.
                    return !token.IsCancellationRequested && store.MatchesCue(cue.Media, cue.Path, cue.Sheet, cue.Start, cue.End)
                        && !token.IsCancellationRequested && CueSheetMatches(cue) && !token.IsCancellationRequested
                        && store.MatchesCue(cue.Media, cue.Path, cue.Sheet, cue.Start, cue.End);
                }
                finally { probeSlots.Release(); }
            }, token);
            try
            {
                while (!probe.Wait(20, token))
                    if (Aborted()) return;
                if (probe.Result && !Aborted() && paths.ContainsKey(alias))
                {
                    mpv_set_property_string(client, "stream-open-filename", cue.Path);
                    return;
                }
            }
            catch (OperationCanceledException) { }
            catch (AggregateException e) when (e.InnerException is OperationCanceledException) { }
            finally { deadline.Cancel(); }

            // Leaving the private URL in place lets mpv skip this occurrence
            // and keeps native indices, history, and saved queue intact.
            if (!Aborted() && entry.HasValue)
                skipped.AddOrUpdate(entry.Value, 1, (_, count) => count + 1);
        }

        // Read only the small CUE document. Never invoke Scanner.Cue here: it
        // opens and probes every referenced audio file. Content is checked on
        // every load, even if an editor preserved length and timestamp.
        static bool CueSheetMatches(CueSource expected)
        {
            try
            {
                using var input = new FileStream(expected.Sheet, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                const int limit = 2 * 1024 * 1024;
                if (input.Length > limit) return false;
                using var memory = new MemoryStream();
                byte[] buffer = new byte[16384]; int read;
                while ((read = input.Read(buffer)) != 0)
                {
                    if (memory.Length + read > limit) return false;
                    memory.Write(buffer, 0, read);
                }
                byte[] bytes = memory.ToArray();
                string document;
                try { document = new System.Text.UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
                catch (System.Text.DecoderFallbackException)
                {
                    System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                    document = System.Text.Encoding.GetEncoding(936).GetString(bytes);
                }
                var tracks = new List<(uint Track, string Path, double Start)>();
                string audio = ""; uint? track = null; bool indexed = false;
                foreach (string raw in document.Split('\n'))
                {
                    string line = raw.Trim();
                    var file = System.Text.RegularExpressions.Regex.Match(line, "^FILE\\s+(?:\"([^\"]+)\"|(\\S+))", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (file.Success)
                    {
                        audio = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(expected.Sheet)!, file.Groups[1].Success ? file.Groups[1].Value : file.Groups[2].Value));
                        continue;
                    }
                    var number = System.Text.RegularExpressions.Regex.Match(line, @"^TRACK\s+(\d+)\s+AUDIO", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (number.Success)
                    {
                        if (track.HasValue && !indexed) return false;
                        if (!uint.TryParse(number.Groups[1].Value, out uint value) || audio.Length == 0) return false;
                        track = value; indexed = false; continue;
                    }
                    var index = System.Text.RegularExpressions.Regex.Match(line, @"^INDEX\s+01\s+(\d+):(\d+):(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (index.Success)
                    {
                        if (!track.HasValue || indexed || !int.TryParse(index.Groups[1].Value, out int minute)
                            || !int.TryParse(index.Groups[2].Value, out int second) || second >= 60
                            || !int.TryParse(index.Groups[3].Value, out int frame) || frame >= 75) return false;
                        tracks.Add((track.Value, audio, minute * 60d + second + frame / 75d)); indexed = true;
                    }
                }
                if (!indexed || tracks.Count == 0 || tracks.Select(t => t.Path.ToUpperInvariant() + "|" + t.Track).Distinct().Count() != tracks.Count) return false;
                for (int i = 0; i < tracks.Count; i++)
                {
                    var item = tracks[i];
                    double? end = i + 1 < tracks.Count && tracks[i + 1].Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase) ? tracks[i + 1].Start : null;
                    if (end.HasValue && end <= item.Start) return false;
                    if (item.Track == expected.Track && item.Path.Equals(expected.DeclaredPath, StringComparison.OrdinalIgnoreCase))
                        return Math.Abs(item.Start - expected.Start) < .000001 && end == expected.End;
                }
                return false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
        }

        public void Dispose()
        {
            closing.Cancel();
            mpv_wakeup(client);
            pump.Join();
            mpv_destroy(client);
            closing.Dispose();
        }
    }
}
