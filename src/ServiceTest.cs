// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.Diagnostics;
using System.Text.Json;
namespace LocalMedia;

static class ServiceTest
{
    public static void Run(string data, string samples)
    {
        Directory.CreateDirectory(data);
        var result = new List<string>();
        void Check(bool ok, string message)
        {
            result.Add((ok ? "PASS " : "FAIL ") + message);
            File.WriteAllLines(Path.Combine(data, "service-test.txt"), result);
            if (!ok)
                throw new Exception(message);
        }
        using var store = new Store(Path.Combine(data, "database"));
        var audio = Scanner.Read(Path.Combine(samples, "flac16.flac"));
        var video = Scanner.Read(Path.Combine(samples, "sample-1080.mkv"));
        store.Upsert([audio, video]);
        using var play = new Playback(store, 0, true);
        void Pump(double seconds)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < end)
            {
                play.Tick();
                Thread.Sleep(20);
            }
        }
        play.Play([audio], audio);
        Pump(.8);
        Check(play.Current?.Id == audio.Id && play.Engine.Number("time-pos") > .2, "real FLAC decode through production service (null output)");
        double first = audio.Position;
        play.Play([video], video);
        Pump(.8);
        Check(play.Current?.Id == video.Id && store.State.Music.Queue.SequenceEqual(new[] { audio.Id }) && store.State.Music.Position > .2, "cross-media switch preserves music queue and position");
        play.Resume("music");
        Pump(.4);
        Check(play.Current?.Id == audio.Id && audio.Position >= first && store.State.Video.Queue.SequenceEqual(new[] { video.Id }), "resume music preserves separate video queue");
        string root = Path.Combine(data, "large-library");
        Directory.CreateDirectory(root);
        for (int i = 0; i < 1000; i++)
            File.Copy(Path.Combine(samples, "mp3.mp3"), Path.Combine(root, $"generated-{i:0000}.mp3"), true);
        var sw = Stopwatch.StartNew();
        var scan = Scanner.Scan(store, root, new Progress<string>(), CancellationToken.None);
        while (!scan.IsCompleted)
        {
            play.Tick();
            Thread.Sleep(20);
        }
        scan.GetAwaiter().GetResult();
        sw.Stop();
        Check(store.Media.Count == 1002, "1000 real generated media files indexed while production service polls events");
        result.Add($"Measured scan wall time: {sw.Elapsed.TotalSeconds:F3} seconds. Not a UI latency or audible output test.");
        store.Backup(Path.Combine(data, "large-backup.db"));
        store.Media.First(m => m.Id == audio.Id).Favorite = true;
        store.Save();
        string persistedId = audio.Id;
        play.Clear();
        store.Restore(Path.Combine(data, "large-backup.db"));
        Check(store.Media.Any(m => m.Id == persistedId) && store.State.Music.Queue.Contains(persistedId), "large library backup restores session references");
        result.Add("未验证：UI响应、设备输出、耳机拔插、睡眠恢复、长时间资源趋势。");
        File.WriteAllLines(Path.Combine(data, "service-test.txt"), result);
    }
}
