// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.Text.Json;
namespace LocalMedia;

static class MediaVerification
{
    public static void RenderPcm(string data, string source)
    {
        Directory.CreateDirectory(data);
        bool loaded = false, ended = false; int error = 0;
        using (var mpv = new Mpv(0, pcm: Path.Combine(data, "render.wav")))
        {
            mpv.EventReceived += (id, e) => { if (id == 8) loaded = true; if (id == 7) { ended = true; error = e; } };
            mpv.Command("loadfile", source);
            DateTime deadline = DateTime.UtcNow.AddSeconds(120);
            while (DateTime.UtcNow < deadline && !(loaded && ended)) { mpv.Poll(); Thread.Sleep(10); }
            File.WriteAllText(Path.Combine(data, "render-result.json"), JsonSerializer.Serialize(new { Source = Path.GetFileName(source), FormatNote = WavPackInfo.Describe(source), Loaded = loaded, Ended = ended, Error = error, Mpv = mpv.Get("mpv-version"), Output = "s16 stereo 44100 PCM file, not device output" }));
        }
        if (!loaded || !ended || error < 0) throw new InvalidDataException("样本PCM输出未完成，请检查render-result.json。");
    }
    public static void Run(string data, string samples)
    {
        var results = new List<object>();
        foreach (string f in Directory.EnumerateFiles(samples).Where(p => Scanner.Extensions.Contains(Path.GetExtension(p))))
        {
            using var mpv = new Mpv(0, true);
            bool loaded = false, advanced = false, ended = false;
            int error = 0;
            mpv.EventReceived += (id, e) => { if (id == 8) loaded = true; if (id == 7) { ended = true; error = e; } };
            mpv.Command("loadfile", f);
            DateTime until = DateTime.UtcNow.AddSeconds(9);
            string codec = "", tracks = "", ao = "";
            double duration = 0, max = 0;
            while (DateTime.UtcNow < until && !ended)
            {
                mpv.Poll();
                if (loaded)
                {
                    double pos = mpv.Number("time-pos");
                    max = Math.Max(max, pos);
                    advanced |= pos > .3;
                    codec = mpv.Get("audio-codec-name") + " / " + mpv.Get("video-codec");
                    tracks = mpv.Get("track-list");
                    duration = Math.Max(duration, mpv.Number("duration"));
                    ao = mpv.Get("current-ao");
                    if (advanced && pos > 1.2)
                        break;
                }
                Thread.Sleep(15);
            }
            results.Add(new
            {
                File = Path.GetFileName(f),
                Loaded = loaded,
                DecodedTimelineAdvanced = advanced,
                Error = error,
                Codec = codec,
                Duration = duration,
                MaximumPosition = max,
                AudioOutput = ao,
                Tracks = tracks,
                Meaning = "Actual decode with null outputs; picture, sound and subtitle visual correctness NOT established"
            });
        }
        File.WriteAllText(Path.Combine(data, "compatibility.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        string video = Path.Combine(samples, "sample-1080.mkv");
        var m = Scanner.Read(video);
        Enrichment.Probe(m, CancellationToken.None).GetAwaiter().GetResult();
        Enrichment.Thumbnail(m, Path.Combine(data, "cache"), CancellationToken.None).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(data, "probe.json"), JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true }));
    }
}
