// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.Text.Json;
namespace LocalMedia;

static class SelfTest
{
    public static void Run(string data)
    {
        Directory.CreateDirectory(data);
        var evidence = new List<string>();
        void Assert(bool ok, string name)
        {
            if (!ok)
                throw new Exception("FAIL " + name);
            evidence.Add("PASS " + name);
        }
        string root = Path.Combine(data, "fixture");
        Directory.CreateDirectory(root);
        string legacy = Path.Combine(root, "旧编码.srt"), subtitleText = "1\n00:00:00,000 --> 00:00:03,000\n简体中文音乐与视频\n";
        File.WriteAllText(legacy, subtitleText, System.Text.Encoding.GetEncoding(936));
        byte[] subtitleOriginal = File.ReadAllBytes(legacy);
        string normalized = Subtitles.Prepare(legacy, "gb18030", Path.Combine(data, "cache"));
        Assert(File.ReadAllText(normalized, System.Text.Encoding.UTF8) == subtitleText && File.ReadAllBytes(legacy).SequenceEqual(subtitleOriginal), "GBK subtitle UTF8 cache and unchanged original bytes");
        Assert(Subtitles.Prepare(normalized, "big5", Path.Combine(data, "cache")) == normalized, "valid UTF8 remains unchanged across legacy codepage settings");
        using (var monitor = new DirectoryMonitor())
        {
            monitor.Configure([root]);
            Assert(SpinWait.SpinUntil(() => monitor.IsWatching(root), 4000), "background watcher becomes ready");
            monitor.Acknowledge();
            File.WriteAllText(Path.Combine(root, "watcher-test.mp3"), "generated event fixture");
            Assert(SpinWait.SpinUntil(() => monitor.Due, 6000), "real file watcher event and debounce");
            monitor.Acknowledge();
            Assert(!monitor.Due, "watcher acknowledgement");
            monitor.Configure([]);
            File.Delete(Path.Combine(root, "watcher-test.mp3"));
            Thread.Sleep(3200);
            Assert(!monitor.Due, "disabled watcher stops events");
        }
        using (var monitor = new DirectoryMonitor())
        {
            string missingRoot = Path.Combine(root, "watcher-appears-later");
            monitor.Configure([missingRoot]);
            Assert(SpinWait.SpinUntil(() => monitor.RetryDue, 17000), "unavailable watcher schedules retry off UI");
            Directory.CreateDirectory(missingRoot);
            Assert(SpinWait.SpinUntil(() => monitor.RetryDue, 17000), "failed directory watcher schedules a bounded retry");
            monitor.Retry();
            Assert(SpinWait.SpinUntil(() => monitor.IsWatching(missingRoot), 4000), "retry creates watcher off UI");
            monitor.Acknowledge();
            File.WriteAllText(Path.Combine(missingRoot, "restored.flac"), "owned watcher fixture");
            Assert(SpinWait.SpinUntil(() => monitor.Due, 6000), "previously unavailable directory watcher recovers after retry");
        }
        Assert(!SystemIntegration.IsRemovedOutput("auto", "default-device", "unrelated-device") && SystemIntegration.IsRemovedOutput("auto", "default-device", "default-device"), "default output ignores unrelated device removal");
        Assert(!SystemIntegration.IsRemovedOutput("wasapi/selected-endpoint", "default-device", "unrelated-device") && SystemIntegration.IsRemovedOutput("wasapi/selected-endpoint", "default-device", "prefix-selected-endpoint-suffix"), "selected output only pauses for its own removed endpoint");
        string wav = Path.Combine(root, "连续专辑.wav");
        using (var writer = new BinaryWriter(File.Create(wav)))
        {
            int n = 44100 * 4;
            writer.Write("RIFF"u8);
            writer.Write(36 + n * 4);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)2);
            writer.Write(44100);
            writer.Write(44100 * 4);
            writer.Write((short)4);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(n * 4);
            for (int i = 0; i < n; i++)
            {
                short s = (short)(Math.Sin(2 * Math.PI * 440 * i / 44100) * 4000);
                writer.Write(s);
                writer.Write(s);
            }
        }
        string cue = Path.Combine(root, "专辑.cue");
        File.WriteAllText(cue, "PERFORMER \"LocalMedia generated test\"\nTITLE \"连续测试\"\nFILE \"连续专辑.wav\" WAVE\n TRACK 01 AUDIO\n TITLE \"第一段\"\n INDEX 01 00:00:00\n TRACK 02 AUDIO\n TITLE \"第二段\"\n INDEX 01 00:02:00\n");
        var tracks = Scanner.Cue(cue);
        Assert(tracks.Count == 2 && tracks[0].End == 2 && tracks[1].Start == 2, "CUE boundaries");
        string dbPath = Path.Combine(data, "database");
        using (var s = new Store(dbPath))
        {
            s.Upsert(tracks);
            s.Media[0].Favorite = true;
            s.Media[0].Position = 1;
            s.State.Lists.Add(new MediaList { Name = "测试", Items = [s.Media[0].Id] });
            s.Save();
            s.Backup(Path.Combine(data, "backup.db"));
            s.Media[0].Favorite = false;
            s.Save();
            s.Restore(Path.Combine(data, "backup.db"));
            Assert(s.Media[0].Favorite && s.State.Lists.Count == 1, "backup/restore metadata");
            s.Upsert(Scanner.Cue(cue));
            Assert(s.Media.Count == 2 && s.Media[0].Favorite, "rescan preserves IDs and favorites");
            string relocated = Path.Combine(data, "relocated");
            Directory.Move(root, relocated);
            s.Relink(root, relocated);
            Assert(s.Media.All(m => m.Online) && s.State.Lists[0].Items[0] == s.Media[0].Id, "root relocation preserves playlist association");
            Directory.Move(relocated, root);
            Assert(s.Media.All(m => !m.Online) && s.Media[0].Favorite, "offline retains data");
            s.Relink(relocated, root);
            Assert(s.Media.All(m => m.Online), "reconnect root");
        }
        using (var s = new Store(dbPath))
            Assert(s.Media.Count == 2 && s.Media[0].Favorite && s.Media[0].Position == 1, "restart persistence");
        using (var mpv = new Mpv(0, true))
        {
            mpv.Command("loadfile", wav);
            DateTime end = DateTime.UtcNow.AddSeconds(6);
            bool loaded = false, advanced = false;
            mpv.EventReceived += (id, e) => { if (id == 8) loaded = true; };
            while (DateTime.UtcNow < end)
            {
                mpv.Poll();
                if (mpv.Number("time-pos") > .5)
                    advanced = true;
                Thread.Sleep(20);
            }
            Assert(loaded && advanced, "libmpv real WAV decode, null output (NOT audible playback)");
            evidence.Add("mpv " + mpv.Get("mpv-version"));
        }
        byte[] original = File.ReadAllBytes(wav);
        string one = Path.Combine(root, "one.wav"), two = Path.Combine(root, "two.wav");
        int half = (original.Length - 44) / 2;
        for (int i = 0; i < 2; i++)
        {
            byte[] b = new byte[44 + half];
            Array.Copy(original, b, 44);
            BitConverter.GetBytes(36 + half).CopyTo(b, 4);
            BitConverter.GetBytes(half).CopyTo(b, 40);
            Array.Copy(original, 44 + i * half, b, 44, half);
            File.WriteAllBytes(i == 0 ? one : two, b);
        }
        string output = Path.Combine(data, "gapless-render.wav");
        using (var mpv = new Mpv(0, pcm: output))
        {
            mpv.Command("loadfile", one);
            mpv.Command("loadfile", two, "append");
            DateTime end = DateTime.UtcNow.AddSeconds(12);
            int loaded = 0;
            mpv.EventReceived += (id, e) => { if (id == 8) loaded++; };
            while (DateTime.UtcNow < end)
            {
                mpv.Poll();
                if (loaded >= 2 && mpv.Get("idle-active") == "yes")
                    break;
                Thread.Sleep(20);
            }
            Assert(loaded == 2, "gapless native playlist loaded both segments");
        }
        byte[] rendered = File.ReadAllBytes(output);
        int offset = -1, length = 0;
        for (int i = 12; i + 8 <= rendered.Length;)
        {
            string tag = System.Text.Encoding.ASCII.GetString(rendered, i, 4);
            int len = BitConverter.ToInt32(rendered, i + 4);
            if (tag == "data")
            {
                offset = i + 8;
                length = Math.Min(len, rendered.Length - offset);
                break;
            }
            if (len < 0 || len > rendered.Length - i - 8)
                break;
            i += 8 + len + (len % 2);
        }
        Assert(offset > 0, "PCM output parsed");
        bool exact = length == original.Length - 44 && rendered.AsSpan(offset, length).SequenceEqual(original.AsSpan(44));
        Assert(exact, "decoded PCM concatenation sample equality; input=" + (original.Length - 44) + ", output=" + length + " bytes. Not a device loopback test.");
        string cueOutput = Path.Combine(data, "cue-render.wav");
        using (var cueStore = new Store(Path.Combine(data, "cue-playback")))
        {
            cueStore.UpsertCue(cue, Scanner.Cue(cue));
            cueStore.State.Volume = 100;
            using var player = new Playback(cueStore, 0, false, cueOutput);
            var cueTracks = cueStore.Media;
            DateTime end = DateTime.UtcNow.AddSeconds(12);
            int loaded = 0;
            player.Engine.EventReceived += (id, _) => { if (id == 8) loaded++; };
            player.PlayAt(cueTracks, 0);
            while (DateTime.UtcNow < end)
            {
                player.Tick();
                if (loaded >= 2 && player.Engine.Get("idle-active") == "yes")
                    break;
                Thread.Sleep(20);
            }
            Assert(loaded == 2 && player.Current?.Id == cueTracks[1].Id && player.Error.Length == 0, "production Playback opened both guarded CUE occurrences");
        }
        byte[] cueRender = File.ReadAllBytes(cueOutput);
        int dataOffset = -1, dataLength = 0;
        for (int i = 12; i + 8 <= cueRender.Length;)
        {
            string tag = System.Text.Encoding.ASCII.GetString(cueRender, i, 4);
            int len = BitConverter.ToInt32(cueRender, i + 4);
            if (tag == "data")
            {
                dataOffset = i + 8;
                dataLength = Math.Min(len, cueRender.Length - dataOffset);
                break;
            }
            if (len < 0 || len > cueRender.Length - i - 8)
                break;
            i += 8 + len + (len % 2);
        }
        bool cueExact = dataOffset > 0 && dataLength == original.Length - 44 && cueRender.AsSpan(dataOffset, dataLength).SequenceEqual(original.AsSpan(44));
        Assert(cueExact, "production CUE same-source segment PCM equality; output=" + dataLength + " bytes; not device loopback");
        File.WriteAllText(Path.Combine(data, "bad.db"), "intentionally invalid test backup");
        using (var s = new Store(dbPath))
        {
            bool rejected = false;
            try
            {
                s.Restore(Path.Combine(data, "bad.db"));
            }
            catch { rejected = true; }
            Assert(rejected && s.Media.Count == 2 && s.Media[0].Favorite, "corrupt backup rejected without replacing current library");
        }
        evidence.Add("未验证：真实扬声器输出、无缝边界、视频与字幕、设备恢复、安装体验。");
        File.WriteAllLines(Path.Combine(data, "self-test.txt"), evidence);
    }
}

