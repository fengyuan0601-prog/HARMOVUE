// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.Text;
namespace LocalMedia;

static class SubtitleServiceTest
{
    public static void Run(string data, string video)
    {
        string fixture = Path.Combine(data, "fixture"); Directory.CreateDirectory(fixture);
        string path = Path.Combine(fixture, "case.mp4"); File.Copy(video, path, true);
        string Srt(string value) => "1\n00:00:00,000 --> 00:02:00,000\n" + value + "\n";
        File.WriteAllText(Path.Combine(fixture, "case.eng.srt"), Srt("English selection test"), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(fixture, "case.zh.srt"), Srt("中文字幕选轨验证"), Encoding.GetEncoding(936));
        string manual = Path.Combine(fixture, "manual.srt"); File.WriteAllText(manual, Srt("Manual external subtitle persisted"), new UTF8Encoding(false));
        var evidence = new List<string>();
        void Expect(Playback player, string text, string label)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10); string actual = "";
            while (DateTime.UtcNow < deadline)
            {
                player.Tick(); actual = player.Engine.Get("sub-text");
                if (actual.Contains(text, StringComparison.Ordinal)) { evidence.Add("PASS " + label + ": " + actual); return; }
                Thread.Sleep(25);
            }
            File.WriteAllLines(Path.Combine(data, "subtitle-service-test.txt"), evidence.Append("FAIL " + label + ": " + actual));
            throw new Exception("字幕服务验证失败：" + label);
        }
        string database = Path.Combine(data, "database");
        using (var store = new Store(database))
        using (var player = new Playback(store, 0, true))
        {
            var media = Scanner.Read(path); store.Upsert([media]); media = store.Media[0];
            store.State.SubtitleLanguages = "zh,eng"; player.Play([media], media);
            Expect(player, "中文字幕", "GBK cache decoded by libmpv and preferred Chinese selected");
            store.State.SubtitleLanguages = "eng,zh"; player.Play([media], media);
            Expect(player, "English selection", "English preference selected over Chinese");
            player.AddSubtitle(manual);
            Expect(player, "Manual external", "manually added external subtitle selected");
            store.Save();
        }
        using (var store = new Store(database))
        using (var player = new Playback(store, 0, true))
        {
            var media = store.Media[0]; player.Play([media], media);
            Expect(player, "Manual external", "manual external path and selected track survive reopen");
            string second = Path.Combine(fixture, "traditional.mp4"); File.Copy(video, second, true);
            File.WriteAllText(Path.ChangeExtension(second, ".srt"), Srt("繁體中文字幕獨立編碼"), Encoding.GetEncoding(950));
            var other = Scanner.Read(second); other.SubCodepage = "big5"; store.Upsert([other]); other = store.Media.Single(m => m.Path == second);
            player.Play([other], other);
            Expect(player, "繁體中文字幕", "Big5 selected for separate media");
            if (media.SubCodepage != "gb18030" || other.SubCodepage != "big5") throw new Exception("字幕编码污染其他文件");
            evidence.Add("PASS per-media codepage remains independent");
            string unicode = Path.Combine(fixture, "unicode.mp4"); File.Copy(video, unicode, true);
            File.WriteAllText(Path.ChangeExtension(unicode, ".srt"), Srt("UTF16 中文字幕实际解码"), new UnicodeEncoding(true, true, true));
            var unicodeMedia = Scanner.Read(unicode); store.Upsert([unicodeMedia]);
            player.Play([unicodeMedia], unicodeMedia);
            Expect(player, "UTF16 中文字幕", "UTF16 BE BOM sidecar normalized and decoded by libmpv");
        }
        evidence.Add("Actual production playback service and libmpv subtitle decoding with null outputs; no physical picture/sound claim.");
        File.WriteAllLines(Path.Combine(data, "subtitle-service-test.txt"), evidence);
    }
}
