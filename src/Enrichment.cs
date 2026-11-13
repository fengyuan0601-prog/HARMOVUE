// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

namespace LocalMedia;

public static class Enrichment
{
    static readonly SemaphoreSlim gate = new(1, 1);
    static readonly SemaphoreSlim probeGate = new(1, 1);
    public static async Task Thumbnail(Media m, string cache, CancellationToken cancel)
    {
        string dest = Path.Combine(cache, m.Id + ".jpg");
        if (m.Kind != "video" || File.Exists(dest))
            return;
        await gate.WaitAsync(cancel);
        try
        {
            // Another request may have completed while this one waited.
            if (File.Exists(dest)) return;
            await Task.Run(() =>
            {
                using var workspace = MediaCache.CreateWorkspace(cache);
                if (workspace == null) return;
                using var mpv = new Mpv(0, true, thumbnail: workspace.DirectoryPath);
                mpv.Command("loadfile", m.Path);
                var end = DateTime.UtcNow.AddSeconds(12);
                while (DateTime.UtcNow < end && !cancel.IsCancellationRequested)
                {
                    mpv.Poll();
                    var image = Directory.EnumerateFiles(workspace.DirectoryPath, "*.jpg").FirstOrDefault();
                    if (image != null)
                    {
                        try
                        {
                            if (new FileInfo(image).Length > MediaCache.WorkspaceBytes) break;
                            using (var check = Image.FromFile(image)) if (check.Width < 1) continue;
                            cancel.ThrowIfCancellationRequested();
                            MediaCache.TryCopy(cache, dest, image);
                            break;
                        }
                        catch (IOException) { } catch (ArgumentException) { }
                    }
                    Thread.Sleep(50);
                }
            }, cancel);
        }
        finally { gate.Release(); }
    }
    public static async Task Probe(Media m, CancellationToken cancel)
    {
        await probeGate.WaitAsync(cancel);
        try
        {
            await Task.Run(() => { using var mpv = new Mpv(0, true); mpv.Set("pause", "yes"); bool ready = false; mpv.EventReceived += (id, e) => { if (id == 8) ready = true; }; mpv.Command("loadfile", m.Path); var until = DateTime.UtcNow.AddSeconds(8); while (!ready && DateTime.UtcNow < until && !cancel.IsCancellationRequested) { mpv.Poll(); Thread.Sleep(15); } if (!ready) return; double duration = mpv.Number("duration"); if (m.Cue.Length == 0 && duration > 0) m.Duration = duration; m.Width = (int)mpv.Number("video-params/w"); m.Height = (int)mpv.Number("video-params/h"); m.Rate = (int)mpv.Number("audio-params/samplerate"); m.Channels = (int)mpv.Number("audio-params/channel-count"); m.Codec = "视频：" + mpv.Get("video-codec") + " / 音频：" + mpv.Get("audio-codec-name"); using var tracks = System.Text.Json.JsonDocument.Parse(mpv.Get("track-list")); bool video = tracks.RootElement.EnumerateArray().Any(t => t.GetProperty("type").GetString() == "video" && (!t.TryGetProperty("albumart", out var art) || !art.GetBoolean())); m.Kind = video ? "video" : "music"; }, cancel);
        }
        finally { probeGate.Release(); }
    }
}
