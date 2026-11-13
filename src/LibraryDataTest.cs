// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LocalMedia;

static class LibraryDataTest
{
    public static void Run(string data)
    {
        Directory.CreateDirectory(data);
        string fixture = Path.Combine(data, "fixture"); Directory.CreateDirectory(fixture);
        var results = new List<string>();
        void Check(bool ok, string label)
        {
            results.Add((ok ? "PASS " : "FAIL ") + label);
            File.WriteAllLines(Path.Combine(data, "library-data-test.txt"), results);
            if (!ok) throw new Exception(label);
        }
        string subtitle = "1\n00:00:00,000 --> 00:00:03,000\n简体与繁體字幕 UTF test\n";
        foreach (var encoding in new Encoding[] { new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true), new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true) })
        {
            string path = Path.Combine(fixture, encoding.CodePage + ".srt"); File.WriteAllText(path, subtitle, encoding);
            var before = SHA256.HashData(File.ReadAllBytes(path));
            string cached = Subtitles.Prepare(path, "gb18030", Path.Combine(data, "cache"));
            Check(File.ReadAllText(cached, new UTF8Encoding(false, true)) == subtitle && before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "BOM subtitle normalized, source unchanged: " + encoding.WebName);
        }
        string playlist = Path.Combine(fixture, "mixed.m3u8");
        string offlineAudio = Path.Combine(fixture, "离线歌曲.flac"), offlineVideo = Path.Combine(fixture, "离线影片.mkv");
        string uriAudio = Path.Combine(fixture, "file uri.mp3");
        File.WriteAllLines(playlist, ["#EXTM3U", "离线歌曲.flac", "离线影片.mkv", "离线歌曲.flac", new Uri(uriAudio).AbsoluteUri, "https://example.invalid/test.mp3", "unsupported.txt", "bad\0.mp3"], new UTF8Encoding(false));
        using (var store = new Store(Path.Combine(data, "database")))
        {
            var result = PlaylistFiles.Import(store, playlist);
            Check(result.Lists.Count == 2 && result.Lists.Single(l => !l.Video).Items.Count == 3 && result.Lists.Single(l => l.Video).Items.Count == 1 && result.Offline == 4 && result.Skipped == 3, "mixed M3U split by kind, duplicates retained, offline retained, URLs/invalid skipped");
            Check(store.Media.Count == 3 && store.Media.Any(m => m.Path == uriAudio), "file URI decoded; repeated path has one physical record");
            var music = result.Lists.Single(l => !l.Video); string firstId = music.Items[0];
            store.Media.First(m => m.Id == firstId).Favorite = true;
            store.Upsert([new Media { Path = offlineAudio, Track = 7, Title = "外部标签修改后的曲目" }]);
            Check(store.Media.Count == 3 && store.Media.Single(m => m.Path == offlineAudio).Id == firstId && store.Media.Single(m => m.Path == offlineAudio).Favorite, "physical track-number metadata update preserves identity/favorite/list references");
            store.Media.First(m => m.Id == firstId).Title = "Line1\n#EXTINF:wrong\rLine2";
            string exported = Path.Combine(fixture, "exported.m3u8"); PlaylistFiles.Export(store, music, exported);
            var imported = PlaylistFiles.Import(store, exported);
            Check(imported.Lists.Single().Items.SequenceEqual(music.Items) && store.Media.Count == 3, "M3U8 order/duplicates/offline identities round-trip; title newline sanitized");
            string sidecar = Path.Combine(fixture, "manual.srt"); File.WriteAllText(sidecar, subtitle);
            var m = store.Media.First(m => m.Id == firstId); m.ExternalSubtitles = [sidecar, fixture + "-sibling\\keep.srt"];
            m.CoverPath = Path.Combine(fixture, "selected-cover.jpg"); m.ResumeMusic = true;
            File.WriteAllBytes(m.CoverPath, [1, 2, 3]); // Path/reference test, not an image decoder fixture.
            store.Upsert([new Media { Path = m.Path, Title = m.Title, Track = m.Track }]);
            Check(m.ResumeMusic && m.CoverPath.EndsWith("selected-cover.jpg"), "rescan preserves selected cover reference and per-file resume choice");
            string destination = Path.Combine(data, "moved"); Directory.Move(fixture, destination); store.Relink(fixture, destination);
            Check(m.ExternalSubtitles[0] == Path.Combine(destination, "manual.srt") && File.Exists(m.ExternalSubtitles[0]) && m.ExternalSubtitles[1] == fixture + "-sibling\\keep.srt", "root relocation moves manual subtitle references only inside exact root boundary");
            Check(music.Items[0] == m.Id && m.Favorite, "relocation preserves playlist and favorite identities");
            Check(m.CoverPath == Path.Combine(destination, "selected-cover.jpg") && File.Exists(m.CoverPath), "root relocation preserves selected cover reference at its new path");
            store.Backup(Path.Combine(data, "complete.db")); m.ExternalSubtitles.Clear(); store.Save(); store.Restore(Path.Combine(data, "complete.db"));
            Check(store.Media.First(x => x.Id == firstId).ExternalSubtitles.Count == 2 && store.State.Lists.Count == 3, "backup restores subtitle references and mixed playlists");
            Check(store.Media.First(x => x.Id == firstId).ResumeMusic && store.Media.First(x => x.Id == firstId).CoverPath == Path.Combine(destination, "selected-cover.jpg"), "backup restores cover reference and per-file resume choice");
            string baseline = JsonSerializer.Serialize(new { Media = store.Media, store.State });
            foreach (var (name, sql) in new[] { ("null-state", "UPDATE state SET body='null'"), ("null-media", "UPDATE media SET body='null'"), ("null-queue", "UPDATE state SET body=json_set(body,'$.Music.Queue',json('null'))") })
            {
                string bad = Path.Combine(data, name + ".db"); File.Copy(Path.Combine(data, "complete.db"), bad, true);
                using (var corrupt = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = bad, Pooling = false }.ToString()))
                {
                    corrupt.Open(); using var command = corrupt.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
                }
                var originalBytes = SHA256.HashData(File.ReadAllBytes(bad)); bool rejected = false;
                try { store.Restore(bad); } catch (InvalidDataException) { rejected = true; }
                Check(rejected && baseline == JsonSerializer.Serialize(new { Media = store.Media, store.State }) && originalBytes.SequenceEqual(SHA256.HashData(File.ReadAllBytes(bad))), "structurally invalid SQLite backup refused before replacing data; source unchanged: " + name);
                using var unlocked = new FileStream(bad, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Check(unlocked.Length > 0, "backup source released for exclusive access after refused restore: " + name);
            }
        }
        using (var reopened = new Store(Path.Combine(data, "database")))
            Check(reopened.Media.Count == 3 && reopened.State.Lists.Count == 3 && reopened.Media.Any(m => m.Favorite && m.ExternalSubtitles.Count == 2), "restart preserves imported offline library and migrated subtitle references");
        using (var named = new Store(Path.Combine(data, "database;semicolon")))
            Check(named.SchemaVersion == 2, "database path containing semicolon handled as literal path");
        using (var unlocked = new FileStream(Path.Combine(data, "complete.db"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(unlocked.Length > 0, "exported/restored backup released for exclusive access");
        results.Add("Data/file-service checks only; no GUI, physical audio or picture claim.");
        File.WriteAllLines(Path.Combine(data, "library-data-test.txt"), results);
    }
}
