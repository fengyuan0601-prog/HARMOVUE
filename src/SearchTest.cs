// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.Diagnostics;
using System.Text.Json;

namespace LocalMedia;

internal static class SearchTest
{
    public static void Run(string data)
    {
        Directory.CreateDirectory(data);
        var log = new List<string>();
        void Check(bool ok, string label)
        {
            log.Add((ok ? "PASS " : "FAIL ") + label);
            File.WriteAllLines(Path.Combine(data, "search-test.txt"), log);
            if (!ok) throw new Exception(label);
        }
        var index = new MediaSearch();
        Media music = new() { Id = "music", Title = "夜曲", Artist = "周杰伦", Album = "十一月的萧邦", Path = @"E:\样本\夜曲.flac" };
        Media video = new() { Id = "video", Title = "夜曲现场", Kind = "video", Category = "音乐视频", Path = @"E:\影片\夜曲现场.mkv" };
        Media other = new() { Id = "other", Title = "重庆森林", AlbumArtist = "王菲", Artist = "Beyoncé", Path = @"E:\Samples\chungking.mkv" };
        var records = new[] { music, video, other };
        HashSet<string> Find(string term) => index.FindAsync(records.Select(SearchDocument.From).ToArray(), term, default).GetAwaiter().GetResult();
        var initial = Stopwatch.StartNew();
        Check(Find("夜曲").SetEquals(["music", "video"]), "literal Chinese finds music and video without changing kind");
        long initialMilliseconds = initial.ElapsedMilliseconds;
        Check(Find("YE QU").SetEquals(["music", "video"]), "case-insensitive spaced full pinyin finds both libraries");
        Check(Find("zjl").SetEquals(["music"]), "artist initials find actual Chinese metadata");
        Check(Find("shiyiyuedexiaobang").SetEquals(["music"]), "album full pinyin finds music");
        Check(Find("yinyeshipin").Count == 0 && Find("yinyueshipin").SetEquals(["video"]), "custom video category searchable without accepting misspelling");
        Check(Find("chongqing").SetEquals(["other"]) && Find("wangfei").SetEquals(["other"]), "phrase polyphone and album artist indexed");
        Check(Find("ＢＥＹＯＮＣＥ").SetEquals(["other"]) && Find("chungking.mkv").SetEquals(["other"]), "Latin accents/full-width query and literal filename search");
        music.Title = "晴天"; music.Path = @"E:\迁移\晴天.flac";
        Check(Find("qingtian").SetEquals(["music"]) && !Find("yequ").Contains("music"), "metadata/path edits replace cached aliases without stale title hits");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); bool cancelled = false;
            try { index.FindAsync(records.Select(SearchDocument.From).ToArray(), "yequ", cancel.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && Find("qingtian").SetEquals(["music"]), "cancelled query does not poison following query");
        }
        var split = new[] { new SearchDocument("split", "夜\n曲") };
        Check(index.FindAsync(split, "yequ", default).GetAwaiter().GetResult().Count == 0, "pinyin never crosses unrelated metadata fields");
        var large = Enumerable.Range(0, 10000).Select(i => new SearchDocument(i.ToString(), $"测试歌曲{i}\n歌手{i % 100}\n专辑{i % 200}\nE:\\离线样本\\测试歌曲{i}.flac")).ToArray();
        var watch = Stopwatch.StartNew();
        var first = index.FindAsync(large, "ceshigequ9876", default).GetAwaiter().GetResult();
        long cold = watch.ElapsedMilliseconds; watch.Restart();
        var second = index.FindAsync(large, "csgq9876", default).GetAwaiter().GetResult();
        long warm = watch.ElapsedMilliseconds;
        Check(first.SetEquals(["9876"]) && second.SetEquals(["9876"]), "10000 synthetic metadata records return exact full-pinyin/initial matches");
        File.WriteAllText(Path.Combine(data, "search-performance.json"), JsonSerializer.Serialize(new { Records = large.Length, InitialThreeRecordQueryMilliseconds = initialMilliseconds, ColdIndexMilliseconds = cold, WarmIndexMilliseconds = warm, DictionaryAlreadyLoadedForLargeCase = true, Meaning = "Actual backend metadata search; not GUI responsiveness or disk scanning evidence." }));
        log.Add("Not a GUI or playback test; aliases stay in memory and never rewrite media tags or database records.");
        File.WriteAllLines(Path.Combine(data, "search-test.txt"), log);
    }
}
