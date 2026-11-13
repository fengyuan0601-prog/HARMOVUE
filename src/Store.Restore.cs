// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LocalMedia;

public sealed partial class Store
{
    const string OfficialSchema = "CREATE TABLE media(id TEXT PRIMARY KEY,identity TEXT UNIQUE NOT NULL,body TEXT NOT NULL);"
        + "CREATE TABLE state(id INTEGER PRIMARY KEY CHECK(id=1),body TEXT NOT NULL);"
        + "CREATE TABLE migration_history(version INTEGER PRIMARY KEY,applied_utc TEXT NOT NULL);"
        + "CREATE INDEX media_kind ON media(json_extract(body,'$.Kind'));"
        + "INSERT INTO migration_history VALUES(2,strftime('%Y-%m-%dT%H:%M:%fZ','now'));PRAGMA user_version=2;";

    sealed record RestoredData(List<Media> Media, AppState State);
    public void ValidateBackup(string file) => ReadBackup(file);

    RestoredData ReadBackup(string file)
    {
        if (System.IO.Path.GetFullPath(file).Equals(System.IO.Path.Combine(DirectoryPath, "library.db"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("请选择导出的备份文件");
        try
        {
            using var source = Connect(file, SqliteOpenMode.ReadOnly);
            source.Open();
            using var check = source.CreateCommand();
            check.CommandText = "PRAGMA integrity_check";
            if ((string?)check.ExecuteScalar() != "ok") throw new InvalidDataException("备份损坏，当前数据未替换。");
            check.CommandText = "PRAGMA user_version";
            int version = Convert.ToInt32(check.ExecuteScalar());
            if (version is not (1 or 2)) throw new InvalidDataException("不支持此备份版本。");
            ValidateBackupSchema(source, version);
            var media = new List<Media>(); var ids = new HashSet<string>(StringComparer.Ordinal); var keys = new HashSet<string>(StringComparer.Ordinal);
            check.CommandText = "SELECT id,identity,body FROM media";
            using (var rows = check.ExecuteReader())
                while (rows.Read())
                {
                    var item = JsonSerializer.Deserialize<Media>(rows.GetString(2));
                    if (item == null) throw new InvalidDataException("备份的媒体记录为空。");
                    ValidateMedia(item);
                    if (item.Id != rows.GetString(0) || item.Key != rows.GetString(1) || !ids.Add(item.Id) || !keys.Add(item.Key))
                        throw new InvalidDataException("备份的媒体身份重复或不匹配。");
                    media.Add(item);
                }
            check.CommandText = "SELECT id,body FROM state";
            AppState state;
            using (var rows = check.ExecuteReader())
            {
                if (!rows.Read() || rows.GetInt64(0) != 1) throw new InvalidDataException("备份缺少有效设置。");
                state = JsonSerializer.Deserialize<AppState>(rows.GetString(1)) ?? throw new InvalidDataException("备份的设置为空。");
                if (rows.Read()) throw new InvalidDataException("备份的设置重复。");
            }
            ValidateState(state, media.ToDictionary(m => m.Id, StringComparer.Ordinal));
            return new(media, state);
        }
        catch (Exception error) when (error is JsonException or SqliteException or ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new InvalidDataException("备份结构或数据无效，当前数据未替换。", error);
        }
    }

    static void ValidateBackupSchema(SqliteConnection source, int version)
    {
        var tables = new HashSet<string>(StringComparer.Ordinal);
        using (var command = source.CreateCommand())
        {
            command.CommandText = "SELECT type,name,tbl_name FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%'";
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                string type = rows.GetString(0), name = rows.GetString(1), owner = rows.GetString(2);
                bool official = type == "table" && name == owner && (name is "media" or "state" || version == 2 && name == "migration_history")
                    || type == "index" && version == 2 && name == "media_kind" && owner == "media";
                if (!official) throw new InvalidDataException("备份包含不支持的数据库结构，当前数据未替换。");
                if (type == "table") tables.Add(name);
            }
        }
        if (!tables.Contains("media") || !tables.Contains("state") || version == 2 && !tables.Contains("migration_history"))
            throw new InvalidDataException("备份的数据库结构不完整。");
        CheckColumns("media", [("id", "TEXT", 0, 1), ("identity", "TEXT", 1, 0), ("body", "TEXT", 1, 0)]);
        CheckColumns("state", [("id", "INTEGER", 0, 1), ("body", "TEXT", 1, 0)]);
        if (version == 2) CheckColumns("migration_history", [("version", "INTEGER", 0, 1), ("applied_utc", "TEXT", 1, 0)]);
        void CheckColumns(string table, (string Name, string Type, int NotNull, int Primary)[] expected)
        {
            using var command = source.CreateCommand();
            command.CommandText = "PRAGMA table_info(" + table + ")";
            using var rows = command.ExecuteReader(); int index = 0;
            while (rows.Read())
            {
                if (index >= expected.Length) throw new InvalidDataException("备份包含未知字段。");
                var column = expected[index++];
                if (rows.GetString(1) != column.Name || !rows.GetString(2).Equals(column.Type, StringComparison.OrdinalIgnoreCase) || rows.GetInt32(3) != column.NotNull || rows.GetInt32(5) != column.Primary)
                    throw new InvalidDataException("备份字段定义无效。");
            }
            if (index != expected.Length) throw new InvalidDataException("备份缺少必要字段。");
        }
    }
    static bool ValidPath(string? path, bool optional = false)
    {
        if (path == null) return false;
        if (path.Length == 0) return optional;
        return path.Length <= 32767 && System.IO.Path.IsPathFullyQualified(path) && path.IndexOf('\0') < 0;
    }
    static bool Nonnegative(double value) => double.IsFinite(value) && value >= 0;
    static void CheckStrings<T>(T value)
    {
        foreach (var property in typeof(T).GetProperties().Where(p => p.PropertyType == typeof(string)))
            if (property.GetValue(value) is not string text || text.Length > 1_000_000)
                throw new InvalidDataException("备份包含空值或过长文字字段。");
    }
    static void ValidateMedia(Media item)
    {
        CheckStrings(item);
        if (string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 256 || !ValidPath(item.Path) || !ValidPath(item.Cue, true) || !ValidPath(item.CoverPath, true)
            || !ValidPath(item.CueDeclaredPath, true) || item.Cue.Length == 0 && item.CueDeclaredPath.Length != 0
            || item.Kind is not ("music" or "video") || item.Watch is not ("未看" or "未看完" or "已看")
            || !Nonnegative(item.Duration) || !Nonnegative(item.Start) || item.End is double end && (!Nonnegative(end) || end <= item.Start)
            || !Nonnegative(item.Position) || !Nonnegative(item.WatchedSeconds) || !double.IsFinite(item.SubDelay) || !double.IsFinite(item.AudioDelay)
            || item.Rate < 0 || item.Bits < 0 || item.Channels < 0 || item.Width < 0 || item.Height < 0 || item.Size < 0
            || item.ExternalSubtitles == null || item.ExternalSubtitles.Any(path => !ValidPath(path))
            || item.Bookmarks == null || item.Bookmarks.Any(value => !Nonnegative(value)))
            throw new InvalidDataException("备份的媒体记录不完整或数值无效。");
    }
    static void ValidateState(AppState state, Dictionary<string, Media> media)
    {
        CheckStrings(state);
        if (state.Roots == null || state.Roots.Any(root => root == null || !ValidPath(root.Path))
            || state.Lists == null || state.Lists.Any(list => list == null || list.Name == null || list.Items == null
                || list.Items.Any(id => id == null || !media.TryGetValue(id, out var item) || (item.Kind == "video") != list.Video))
            || state.Music == null || state.Video == null || state.Shortcuts == null || state.Shortcuts.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null)
            || state.Active is not ("music" or "video") || state.Repeat is not ("off" or "one" or "all") || state.Theme is not ("dark" or "light")
            || state.Volume is < 0 or > 100 || !double.IsFinite(state.SubtitleSize) || state.SubtitleSize is <= 0 or > 1000
            || !double.IsFinite(state.SubtitlePosition) || state.SubtitlePosition is < 0 or > 100)
            throw new InvalidDataException("备份的设置、歌单或目录记录无效。");
        CheckSession(state.Music, "music"); CheckSession(state.Video, "video");
        void CheckSession(Session session, string kind)
        {
            if (session.Queue == null || session.History == null || !Nonnegative(session.Position) || !double.IsFinite(session.Speed) || session.Speed is < .1 or > 16
                || session.Index < 0 || (session.Queue.Count == 0 ? session.Index != 0 : session.Index >= session.Queue.Count)
                || session.History.Any(index => index < 0 || index >= session.Queue.Count)
                || session.Queue.Any(id => id == null || !media.TryGetValue(id, out var item) || item.Kind != kind))
                throw new InvalidDataException("备份的播放队列或历史记录无效。");
        }
    }

    public void Restore(string file)
    {
        // Never copy source SQLite objects into the live library. Only typed,
        // validated records enter a freshly created official schema.
        var restored = ReadBackup(file);
        string stage = System.IO.Path.Combine(DirectoryPath, "restore-stage-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var staged = Connect(stage); staged.Open();
            using (var create = staged.CreateCommand()) { create.CommandText = OfficialSchema; create.ExecuteNonQuery(); }
            using (var transaction = staged.BeginTransaction())
            {
                using var insert = staged.CreateCommand(); insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO media VALUES($id,$key,$body)";
                var id = insert.Parameters.Add("$id", SqliteType.Text); var key = insert.Parameters.Add("$key", SqliteType.Text); var body = insert.Parameters.Add("$body", SqliteType.Text);
                foreach (var item in restored.Media)
                { id.Value = item.Id; key.Value = item.Key; body.Value = JsonSerializer.Serialize(item); insert.ExecuteNonQuery(); }
                insert.Parameters.Clear(); insert.CommandText = "INSERT INTO state VALUES(1,$body)";
                insert.Parameters.AddWithValue("$body", JsonSerializer.Serialize(restored.State)); insert.ExecuteNonQuery(); transaction.Commit();
            }
            lock (gate)
            {
                string rollback = System.IO.Path.Combine(DirectoryPath, $"before-restore-{DateTime.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.db");
                Backup(rollback);
                try { staged.BackupDatabase(db); Load(); }
                catch { using var original = Connect(rollback, SqliteOpenMode.ReadOnly); original.Open(); original.BackupDatabase(db); Load(); throw; }
            }
        }
        finally
        {
            // These exact generated staging paths never include user media.
            foreach (string temporary in new[] { stage, stage + "-wal", stage + "-shm" })
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
        }
    }
}
