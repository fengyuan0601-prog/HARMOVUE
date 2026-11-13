// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace LocalMedia;

public sealed class Media
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = ""; public string Title { get; set; } = "";
    public string Artist { get; set; } = ""; public string Album { get; set; } = "";
    // User choices belong to persistent library data, never the evictable cache.
    public string CoverPath { get; set; } = "";
    public bool ResumeMusic { get; set; }
    public string AlbumArtist { get; set; } = ""; public uint Disc
    {
        get; set;
    }
    public uint Track
    {
        get; set;
    }
    public string Kind { get; set; } = "music"; public string Category { get; set; } = "未分类";
    public double Duration
    {
        get; set;
    }
    public double Start
    {
        get; set;
    }
    public double? End
    {
        get; set;
    }
    public string FormatNote {get;set;} = "";
    // An explicit file relocation maps a CUE declaration to a physical source
    // without rewriting the user's CUE. Empty means the declaration is Path.
    public string CueDeclaredPath { get; set; } = "";
    public string Cue { get; set; } = ""; public bool CueTrackMissing { get; set; } public string Codec { get; set; } = "未知"; public int Rate
    {
        get; set;
    }
    public int Bits
    {
        get; set;
    }
    public int Channels
    {
        get; set;
    }
    public int Width
    {
        get; set;
    }
    public int Height
    {
        get; set;
    }
    public long Size
    {
        get; set;
    }
    public DateTime ModifiedUtc
    {
        get; set;
    }
    public bool Favorite
    {
        get; set;
    }
    public double Position
    {
        get; set;
    }
    public double WatchedSeconds
    {
        get; set;
    }
    public bool WatchManual { get; set; }
    public string Watch { get; set; } = "未看"; public DateTime? LastPlayed
    {
        get; set;
    }
    public string SubCodepage {get;set;} = "gb18030";
    public List<string> ExternalSubtitles {get;set;} = [];
    public string Audio { get; set; } = "auto"; public string Subtitle { get; set; } = "auto";
    public double SubDelay
    {
        get; set;
    }
    public double AudioDelay
    {
        get; set;
    }
    public List<double> Bookmarks { get; set; } = [];
    public DateTime Added { get; set; } = DateTime.UtcNow;
    // Derived values are evaluated when needed, never while persisting a record.
    // In particular, saving playback progress must not probe every media drive.
    [JsonIgnore] public bool Online => !CueTrackMissing && File.Exists(Path) && (Cue.Length == 0 || File.Exists(Cue));
    [JsonIgnore] public string Key => System.IO.Path.GetFullPath(Path).ToUpperInvariant() + "|" + Cue.ToUpperInvariant() + "|" + Track;
    [JsonIgnore] public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    string? sortedTitle, sortedAlbum, titleKey, albumKey;
    [JsonIgnore] public string NaturalTitle => sortedTitle == Title ? titleKey! : titleKey = Scanner.Natural(sortedTitle = Title);
    [JsonIgnore] public string NaturalAlbum => sortedAlbum == Album ? albumKey! : albumKey = Scanner.Natural(sortedAlbum = Album);
    public override string ToString() => Title;
    internal Media Snapshot() => (Media)MemberwiseClone();

    // Scanner-owned fields only: user choices and playback state stay on the
    // existing object, including when a failed batch is rolled back in memory.
    internal void ApplyScanFields(Media source)
    {
        Title = source.Title; Artist = source.Artist; Album = source.Album;
        AlbumArtist = source.AlbumArtist; Disc = source.Disc; Track = source.Track;
        Duration = source.Duration; Start = source.Start; End = source.End;
        Codec = source.Codec; FormatNote = source.FormatNote; Kind = source.Kind;
        Rate = source.Rate; Bits = source.Bits; Channels = source.Channels;
        Width = source.Width; Height = source.Height; Size = source.Size;
        ModifiedUtc = source.ModifiedUtc; CueTrackMissing = source.CueTrackMissing;
    }
}
public sealed class MediaList
{
    public string Name { get; set; } = ""; public List<string> Items { get; set; } = []; public bool Video
    {
        get; set;
    }
}
public sealed class Session
{
    public List<string> Queue { get; set; } = []; public int Index
    {
        get; set;
    }
    public double Position
    {
        get; set;
    }
    public List<int> History { get; set; } = []; public double Speed { get; set; } = 1;
}
public sealed class Root
{
    public string Path { get; set; } = ""; public bool Paused
    {
        get; set;
    }
}
public sealed class AppState
{
    public List<Root> Roots { get; set; } = []; public List<MediaList> Lists { get; set; } = [];
    public Session Music { get; set; } = new(); public Session Video { get; set; } = new();
    public string Active { get; set; } = "music"; public bool RecordHistory { get; set; } = true;
    public bool AutoNext { get; set; } = true; public string Repeat { get; set; } = "off"; public bool Shuffle
    {
        get; set;
    }
    public string Device { get; set; } = "auto"; public int Volume { get; set; } = 70;
    public string Page { get; set; } = "首页";
    public string Theme { get; set; } = "dark";
    public string SubtitleLanguages { get; set; } = "zh,chi,zho,eng";
    public string AudioLanguages { get; set; } = "zh,chi,zho,eng";
    public double SubtitleSize { get; set; } = 55;
    public double SubtitlePosition { get; set; } = 100;
    public bool PeriodicScan {get;set;}=true;
    public bool CloseToTray {get;set;}
    public bool WatchFolders {get;set;}=true;
    public bool OverrideAss
    {
        get; set;
    }
    public Dictionary<string, string> Shortcuts { get; set; } = new() { { "播放暂停", "Space" }, { "下一首", "Control, Right" }, { "上一首", "Control, Left" }, { "前进5秒", "Right" }, { "后退5秒", "Left" }, { "全屏", "F11" } };
}
public sealed partial class Store : IDisposable
{
    static SqliteConnection Connect(string file, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        // The caller also measures this call: work before this method body (for
        // example first-entry preparation) is not included in this inner scope.
        using var measure = UiPerformance.Measure("sqlite_connect_body");
        SqliteConnectionStringBuilder builder;
        using (UiPerformance.Measure("sqlite_connection_string_builder"))
            builder = new SqliteConnectionStringBuilder { DataSource = file, Mode = mode, Pooling = false };
        string connectionString;
        using (UiPerformance.Measure("sqlite_connection_string_serialize")) connectionString = builder.ToString();
        using (UiPerformance.Measure("sqlite_connection_constructor")) return new SqliteConnection(connectionString);
    }
    public readonly string DirectoryPath; readonly SqliteConnection db; readonly object gate = new(); readonly Dictionary<string, string> persisted = [];
    string? persistedState;
    int revision;
    public int Revision => Volatile.Read(ref revision);
    private List<Media> items = []; public List<Media> Media
    {
        get
        {
            lock (gate)
                return items.ToList();
        }
    }
    public AppState State { get; private set; } = new();
    internal bool MatchesCue(Media item, string path, string cue, double start, double? end)
    {
        lock (gate)
            return !item.CueTrackMissing && item.Path.Equals(path, StringComparison.OrdinalIgnoreCase)
                && item.Cue.Equals(cue, StringComparison.OrdinalIgnoreCase) && item.Start == start && item.End == end;
    }
    public Store(string path)
    {
        using var measure = UiPerformance.Measure("store_open");
        DirectoryPath = path;
        using (UiPerformance.Measure("store_data_directory")) Directory.CreateDirectory(path);
        using (UiPerformance.Measure("store_cache_directory")) Directory.CreateDirectory(System.IO.Path.Combine(path, "cache"));
        Diagnostics.TraceUi(path, "SQLite connect begin");
        using (UiPerformance.Measure("store_connect_call")) db = Connect(System.IO.Path.Combine(path, "library.db"));
        Diagnostics.TraceUi(path, "SQLite connect end");
        using (UiPerformance.Measure("store_sqlite_open")) db.Open();
        Diagnostics.TraceUi(path, "SQLite open end");
        using (UiPerformance.Measure("store_version_check"))
        using (var version = db.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version";
            if (Convert.ToInt32(version.ExecuteScalar()) > 2)
                throw new InvalidDataException("数据库由更新版本创建，请用更新版本打开；未修改原数据库。");
        }
        using (UiPerformance.Measure("store_schema"))
        {
            Exec("PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; CREATE TABLE IF NOT EXISTS media(id TEXT PRIMARY KEY,identity TEXT UNIQUE NOT NULL,body TEXT NOT NULL); CREATE TABLE IF NOT EXISTS state(id INTEGER PRIMARY KEY CHECK(id=1),body TEXT NOT NULL);");
            Migrate();
        }
        using (UiPerformance.Measure("store_load")) Load();
    }
    void Exec(string sql)
    {
        using var c = db.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }
    public int SchemaVersion {get{lock(gate){using var c=db.CreateCommand();c.CommandText="PRAGMA user_version";return Convert.ToInt32(c.ExecuteScalar());}}}
    public string SQLiteVersion {get{lock(gate){using var c=db.CreateCommand();c.CommandText="SELECT sqlite_version()";return (string)c.ExecuteScalar()!;}}}
    void Migrate()
    {
        using var check = db.CreateCommand();
        check.CommandText = "PRAGMA user_version";
        int version = Convert.ToInt32(check.ExecuteScalar());
        if (version >= 2)
            return;
        using var tx = db.BeginTransaction();
        using var c = db.CreateCommand();
        c.Transaction = tx;
        c.CommandText = "CREATE TABLE IF NOT EXISTS migration_history(version INTEGER PRIMARY KEY,applied_utc TEXT NOT NULL);CREATE INDEX IF NOT EXISTS media_kind ON media(json_extract(body,'$.Kind'));INSERT OR IGNORE INTO migration_history VALUES(2,strftime('%Y-%m-%dT%H:%M:%fZ','now'));PRAGMA user_version=2;";
        c.ExecuteNonQuery();
        tx.Commit();
    }
    void Load()
    {
        State = new();
        items = [];
        persisted.Clear();
        persistedState = null;
        using (var c = db.CreateCommand())
        {
            c.CommandText = "SELECT body FROM media";
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                string json = r.GetString(0);
                var m = JsonSerializer.Deserialize<Media>(json) ?? throw new InvalidDataException("媒体库记录为空，请恢复有效备份。");
                items.Add(m);
                persisted[m.Id] = json;
            }
        }
        using (var c = db.CreateCommand())
        {
            c.CommandText = "SELECT body FROM state WHERE id=1";
            if (c.ExecuteScalar() is string s)
            {
                State = JsonSerializer.Deserialize<AppState>(s) ?? throw new InvalidDataException("媒体库设置为空，请恢复有效备份。");
                persistedState = s;
            }
        }
        RebuildIndexes();
        Interlocked.Increment(ref revision);
    }
    public void Save()
    {
        using var measure = UiPerformance.Measure("store_save", count: items.Count);
        lock (gate)
            SaveCore(items);
    }
    // Playback only mutates the active record and the two session states. Broad
    // user edits, backup and shutdown retain the full-library Save boundary;
    // scanner batches persist their affected records in Upsert below.
    public void SavePlayback(Media? current) => SavePlaybackBatch(current == null ? [] : [current]);
    // A known single-record edit need not serialize the entire library. State
    // (lists/settings/sessions) is committed in the same transaction as before.
    public void SaveMetadata(Media media) => SavePlaybackBatch([media]);
    public void SetWatchState(Media media, string state)
    {
        if (media.Kind != "video" || state is not ("未看" or "未看完" or "已看")) throw new ArgumentException("无效的观看状态");
        media.Watch = state;
        media.WatchManual = true;
        SaveMetadata(media);
    }
    public void ResumeAutomaticWatchState(Media media)
    {
        media.WatchManual = false;
        media.Watch = media.Duration > 0 && media.WatchedSeconds >= media.Duration * .9 ? "已看" : media.Position > 1 ? "未看完" : "未看";
        SaveMetadata(media);
    }
    public void ResetVideoProgress(Media media)
    {
        media.Position = 0;
        media.WatchedSeconds = 0;
        media.Watch = "未看";
        media.WatchManual = false;
        if (State.Video.Queue.ElementAtOrDefault(State.Video.Index) == media.Id) State.Video.Position = 0;
        SaveMetadata(media);
    }
    public void SaveState() => SavePlaybackBatch([]);
    internal void SavePlaybackBatch(IEnumerable<Media> records)
    {
        var changed = records.Distinct().ToArray();
        using var measure = UiPerformance.Measure("store_checkpoint", count: changed.Length);
        lock (gate)
        {
            EnsureIndexes();
            SaveCore(changed.Where(ContainsRecord));
        }
    }
    void SaveCore(IEnumerable<Media> records)
    {
        EnsureIndexes();
        var changed = new Dictionary<string, (string Key, string Json)>();
        foreach (var m in records)
        {
            string json = JsonSerializer.Serialize(m);
            if (!persisted.TryGetValue(m.Id, out var old) || old != json)
                changed[m.Id] = (m.Key, json);
        }
        string state = JsonSerializer.Serialize(State);
        bool stateChanged = state != persistedState;
        if (changed.Count == 0 && !stateChanged) return;
        using var tx = db.BeginTransaction();
        using (var c = db.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = "INSERT INTO media VALUES($i,$k,$b) ON CONFLICT(id) DO UPDATE SET identity=$k,body=$b";
            var id = c.Parameters.Add("$i", SqliteType.Text);
            var key = c.Parameters.Add("$k", SqliteType.Text);
            var body = c.Parameters.Add("$b", SqliteType.Text);
            foreach (var entry in changed)
            {
                id.Value = entry.Key; key.Value = entry.Value.Key; body.Value = entry.Value.Json;
                c.ExecuteNonQuery();
            }
        }
        if (stateChanged)
        {
            using var c = db.CreateCommand(); c.Transaction = tx;
            c.CommandText = "INSERT INTO state VALUES(1,$b) ON CONFLICT(id) DO UPDATE SET body=$b";
            c.Parameters.AddWithValue("$b", state); c.ExecuteNonQuery();
        }
        tx.Commit();
        // Advance snapshots only after the complete transaction is durable.
        foreach (var entry in changed) persisted[entry.Key] = entry.Value.Json;
        persistedState = state;
        foreach (string id in changed.Keys)
            if (byId.TryGetValue(id, out int row)) UpdateIndex(row);
        if (changed.Count > 0) Interlocked.Increment(ref revision);
    }
    public void Upsert(IEnumerable<Media> entries) => UpsertCore(entries, null);

    // A successfully parsed, non-empty CUE is one reconciliation boundary.
    // Missing tracks retain IDs and user data, but cannot be played as online.
    public void UpsertCue(string cue, IEnumerable<Media> entries)
    {
        string full = System.IO.Path.GetFullPath(cue);
        var batch = entries.ToArray();
        if (batch.Length == 0 || batch.Any(m => !m.Cue.Equals(full, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("CUE 解析结果不完整，未更改已有曲目。");
        UpsertCore(batch, full);
    }

    void UpsertCore(IEnumerable<Media> entries, string? cue)
    {
        lock (gate)
        {
            var batch = entries.ToArray();
            if (batch.Length == 0) { SaveCore([]); return; }
            int originalCount = items.Count;
            double musicPosition = State.Music.Position, videoPosition = State.Video.Position;
            var snapshots = new Dictionary<int, Media>();
            void Remember(int index)
            {
                if (index < originalCount && !snapshots.ContainsKey(index)) snapshots.Add(index, items[index].Snapshot());
            }
            try
            {
            // Indexes are maintained as records change. A thirty-row scan
            // batch must not rebuild dictionaries for the entire library.
            EnsureIndexes();
            var affected = new HashSet<int>();
            for (int input = 0; input < batch.Length; input++)
            {
                var m = batch[input];
                string key = m.Key;
                int index = byKey.TryGetValue(key, out var matches) ? matches.Min : int.MaxValue;
                if (m.Cue == "" && byPlainPath.TryGetValue(m.Path, out var paths)) index = Math.Min(index, paths.Min);
                if (index == int.MaxValue && m.Cue.Length != 0 && byCueDeclaration.TryGetValue(key, out var declarations))
                {
                    index = declarations.Min;
                    // Scanner metadata follows the original FILE declaration,
                    // while the explicit user mapping remains authoritative.
                    var mapped = items[index];
                    m = m.Snapshot(); m.CueDeclaredPath = mapped.CueDeclaredPath;
                    m.Path = mapped.Path;
                    // The parser probed the declared source, not the mapped
                    // physical file. Its audio properties must not overwrite
                    // the known properties of the authorized physical source.
                    double total = byPlainPath.TryGetValue(mapped.Path, out var physical) ? items[physical.Min].Duration : 0;
                    if (total <= 0 && mapped.End == null) total = mapped.Duration + mapped.Start;
                    m.Duration = m.End is double end ? end - m.Start : Math.Max(0, total - m.Start);
                    m.Codec = mapped.Codec; m.FormatNote = mapped.FormatNote; m.Rate = mapped.Rate; m.Bits = mapped.Bits; m.Channels = mapped.Channels;
                    m.Width = mapped.Width; m.Height = mapped.Height; m.Size = mapped.Size; m.ModifiedUtc = mapped.ModifiedUtc;
                    batch[input] = m;
                }
                if (index == int.MaxValue)
                {
                    index = items.Count;
                    items.Add(m);
                    AddIndex(index);
                }
                else
                {
                    Remember(index);
                    var old = items[index];
                    bool segmentChanged = old.Cue.Length != 0 && (old.Start != m.Start || old.End != m.End);
                    old.ApplyScanFields(m);
                    old.CueDeclaredPath = m.CueDeclaredPath;
                    if (segmentChanged)
                    {
                        // Progress is relative to the indexed CUE segment.
                        // A changed boundary is a new timeline, while explicit
                        // user watch decisions and bookmarks remain user data.
                        old.Position = 0; old.WatchedSeconds = 0;
                        if (!old.WatchManual) old.Watch = "未看";
                        var session = old.Kind == "video" ? State.Video : State.Music;
                        if (session.Queue.ElementAtOrDefault(session.Index) == old.Id) session.Position = 0;
                    }
                    UpdateIndex(index);
                }
                affected.Add(index);
            }
            if (cue != null)
            {
                var live = batch.Select(m => m.Key).ToHashSet(StringComparer.Ordinal);
                foreach (int index in byCue.TryGetValue(cue, out var cueRows) ? cueRows : [])
                {
                    var existing = items[index];
                    bool missing = !live.Contains(existing.Key);
                    if (existing.CueTrackMissing == missing) continue;
                    Remember(index); existing.CueTrackMissing = missing; affected.Add(index);
                }
            }
            // Preserve the existing row write order and transaction semantics.
            // An empty final scan batch still commits pending session/settings
            // state, without serializing every unrelated media record.
            SaveCore(affected.Order().Select(index => items[index]));
            }
            catch
            {
                foreach (var snapshot in snapshots)
                {
                    var item = items[snapshot.Key]; item.ApplyScanFields(snapshot.Value);
                    item.CueDeclaredPath = snapshot.Value.CueDeclaredPath;
                    item.Position = snapshot.Value.Position; item.WatchedSeconds = snapshot.Value.WatchedSeconds; item.Watch = snapshot.Value.Watch;
                }
                State.Music.Position = musicPosition; State.Video.Position = videoPosition;
                if (items.Count > originalCount) items.RemoveRange(originalCount, items.Count - originalCount);
                RebuildIndexes();
                throw;
            }
        }
    }
    public void Backup(string file)
    {
        if (System.IO.Path.GetFullPath(file).Equals(System.IO.Path.Combine(DirectoryPath, "library.db"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("请选择其他备份文件名");
        Save();
        lock (gate)
        {
            using var dest = Connect(file);
            dest.Open();
            db.BackupDatabase(dest);
        }
    }
    public void Relink(string from, string to)
    {
        lock (gate)
        {
            string destinationRoot = System.IO.Path.GetFullPath(to);
            from = System.IO.Path.GetFullPath(from).TrimEnd('\\');
            to = destinationRoot.TrimEnd('\\');
            string MovePath(string path) => path.StartsWith(from + "\\", StringComparison.OrdinalIgnoreCase) ? to + path[from.Length..] : path;
            var changes = items.Select(m => (Media: m, Before: m.Snapshot(), After: m.Snapshot())).ToArray();
            foreach (var change in changes)
            {
                change.After.Path = MovePath(change.Before.Path);
                change.After.Cue = MovePath(change.Before.Cue);
                change.After.CueDeclaredPath = MovePath(change.Before.CueDeclaredPath);
                change.After.CoverPath = MovePath(change.Before.CoverPath);
                change.After.ExternalSubtitles = change.Before.ExternalSubtitles.Select(MovePath).ToList();
            }
            if (changes.Select(c => c.After.Key).Distinct(StringComparer.Ordinal).Count() != changes.Length)
                throw new IOException("目录迁移会产生重复的媒体记录，未更改资料库。");
            var roots = State.Roots.Where(r => System.IO.Path.GetFullPath(r.Path).TrimEnd('\\').Equals(from, StringComparison.OrdinalIgnoreCase)).Select(r => (Root: r, Original: r.Path)).ToArray();
            try
            {
                foreach (var change in changes)
                {
                    change.Media.Path = change.After.Path; change.Media.Cue = change.After.Cue;
                    change.Media.CueDeclaredPath = change.After.CueDeclaredPath;
                    change.Media.CoverPath = change.After.CoverPath; change.Media.ExternalSubtitles = change.After.ExternalSubtitles;
                }
                foreach (var root in roots) root.Root.Path = destinationRoot;
                SaveCore(items);
            }
            catch
            {
                foreach (var change in changes)
                {
                    change.Media.Path = change.Before.Path; change.Media.Cue = change.Before.Cue;
                    change.Media.CueDeclaredPath = change.Before.CueDeclaredPath;
                    change.Media.CoverPath = change.Before.CoverPath; change.Media.ExternalSubtitles = change.Before.ExternalSubtitles;
                }
                foreach (var root in roots) root.Root.Path = root.Original;
                RebuildIndexes();
                throw;
            }
        }
    }
    public void Dispose()
    {
        Save();
        db.Dispose();
    }
}
public static class Scanner
{
    public static readonly HashSet<string> Extensions = new(".mp3 .aac .m4a .flac .alac .ape .tta .wv .wav .aiff .aif .ogg .opus .wma .mp4 .mkv .mov .avi .webm .flv .wmv .asf .mpg .mpeg .ts .m2ts .rm .rmvb .m4v".Split(' '), StringComparer.OrdinalIgnoreCase);
    public static Media Read(string path)
    {
        var m = new Media { Path = System.IO.Path.GetFullPath(path), Title = System.IO.Path.GetFileNameWithoutExtension(path), Size = new FileInfo(path).Length, ModifiedUtc = File.GetLastWriteTimeUtc(path) };
        try
        {
            using var f = TagLib.File.Create(path);
            var t = f.Tag;
            var p = f.Properties;
            m.Title = string.IsNullOrWhiteSpace(t.Title) ? m.Title : t.Title;
            m.Artist = string.Join(" / ", t.Performers);
            m.Album = t.Album ?? "";
            m.AlbumArtist = string.Join(" / ", t.AlbumArtists);
            m.Disc = t.Disc;
            m.Track = t.Track;
            m.Duration = p.Duration.TotalSeconds;
            m.Rate = p.AudioSampleRate;
            m.Channels = p.AudioChannels;
            m.Bits = p.BitsPerSample;
            m.Width = p.VideoWidth;
            m.Height = p.VideoHeight;
            m.Codec = p.Description;
            m.Kind = p.VideoWidth > 0 ? "video" : "music";
        }
        catch { m.Kind = ".mp4 .mkv .mov .avi .webm .flv .wmv .asf .mpg .mpeg .ts .m2ts .rm .rmvb .m4v".Split(' ').Contains(System.IO.Path.GetExtension(path).ToLowerInvariant()) ? "video" : "music"; }
        m.FormatNote = WavPackInfo.Describe(path);
        return m;
    }
    public static string Text(string path)
    {
        var b = File.ReadAllBytes(path);
        try
        {
            return new UTF8Encoding(false, true).GetString(b).TrimStart('\uFEFF');
        }
        catch { return Encoding.GetEncoding(936).GetString(b); }
    }
    public static List<Media> Cue(string path)
    {
        var result = new List<Media>();
        var indexed = new HashSet<Media>();
        string audio = "", album = "", artist = "";
        Media? current = null;
        foreach (string raw in Text(path).Split('\n'))
        {
            string line = raw.Trim();
            var match = Regex.Match(line, "^FILE\\s+(?:\"([^\"]+)\"|(\\S+))", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                audio = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value));
                continue;
            }
            match = Regex.Match(line, @"^TRACK\s+(\d+)\s+AUDIO", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                if (string.IsNullOrWhiteSpace(audio)) throw new InvalidDataException("CUE 曲目缺少 FILE，未更新已有曲目。");
                current = File.Exists(audio) ? Read(audio) : new Media { Path = audio };
                current.Cue = System.IO.Path.GetFullPath(path);
                current.Track = uint.Parse(match.Groups[1].Value);
                current.Album = album;
                if (!string.IsNullOrWhiteSpace(artist)) current.AlbumArtist = artist;
                current.Artist = artist;
                current.Title = $"曲目 {current.Track:00}";
                current.Kind = "music";
                result.Add(current);
                continue;
            }
            match = Regex.Match(line, "^(TITLE|PERFORMER)\\s+\"?(.*?)\"?$", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                string v = match.Groups[2].Value;
                if (match.Groups[1].Value.Equals("TITLE", StringComparison.OrdinalIgnoreCase))
                {
                    if (current == null)
                        album = v;
                    else
                        current.Title = v;
                }
                else
                {
                    if (current == null)
                        artist = v;
                    else
                        current.Artist = v;
                }
                continue;
            }
            match = Regex.Match(line, @"^INDEX\s+01\s+(\d+):(\d+):(\d+)", RegexOptions.IgnoreCase);
            if (match.Success && current != null)
            {
                int seconds = int.Parse(match.Groups[2].Value), frames = int.Parse(match.Groups[3].Value);
                if (seconds >= 60 || frames >= 75 || !indexed.Add(current)) throw new InvalidDataException("CUE 曲目时间无效，未更新已有曲目。");
                current.Start = int.Parse(match.Groups[1].Value) * 60 + seconds + frames / 75.0;
            }
        }
        if (result.Count == 0 || indexed.Count != result.Count || result.Select(m => m.Key).Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw new InvalidDataException("CUE 轨道不完整或重复，未更新已有曲目。");
        for (int i = 0; i < result.Count; i++)
        {
            var m = result[i];
            double sourceDuration = m.Duration;
            m.End = i + 1 < result.Count && result[i + 1].Path == m.Path ? result[i + 1].Start : null;
            if (m.End is double end && end <= m.Start) throw new InvalidDataException("CUE 轨道时间顺序无效，未更新已有曲目。");
            m.Duration = Math.Max(0, (m.End ?? sourceDuration) - m.Start);
        }
        return result;
    }
    public static async Task Scan(Store store, string root, IProgress<string> progress, CancellationToken cancel)
    {
        await Task.Run(() =>
        {
            var known = store.Media.Where(m => m.Cue.Length == 0).GroupBy(m => m.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var batch = new List<Media>();
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            bool commitFailed = false;
            void Flush() { if (batch.Count == 0) return; try { store.Upsert(batch); batch.Clear(); } catch { commitFailed = true; throw; } }
            try
            {
                foreach (var path in Directory.EnumerateFiles(root, "*", opts))
                {
                    cancel.ThrowIfCancellationRequested();
                    try
                    {
                        string extension = System.IO.Path.GetExtension(path);
                        if (extension.Equals(".cue", StringComparison.OrdinalIgnoreCase))
                        {
                            var tracks = Cue(path); // Parse completely before changing any existing tracks.
                            cancel.ThrowIfCancellationRequested();
                            Flush();
                            try { store.UpsertCue(path, tracks); } catch { commitFailed = true; throw; }
                        }
                        else if (Extensions.Contains(extension))
                        {
                            if (known.TryGetValue(path, out var unchanged) && unchanged.ModifiedUtc == File.GetLastWriteTimeUtc(path) && unchanged.Size == new FileInfo(path).Length)
                            { progress.Report(path); continue; }
                            var m = Read(path);
                            if (m.Kind == "video" || m.Codec == "未知")
                                try { Enrichment.Probe(m, cancel).GetAwaiter().GetResult(); }
                                catch (OperationCanceledException) { throw; } catch { }
                            batch.Add(m);
                        }
                        if (batch.Count >= 30) Flush();
                        progress.Report(path);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) when (commitFailed) { throw; }
                    catch (Exception e) { progress.Report("跳过：" + System.IO.Path.GetFileName(path) + " — " + e.Message); }
                }
            }
            finally { if (!commitFailed) { if (batch.Count > 0) Flush(); else store.Upsert([]); } }
        }, cancel);
    }
    public static string Natural(string s)
    {
        string upper = s.ToUpperInvariant();
        int first = 0;
        while (first < upper.Length && !char.IsDigit(upper[first])) first++;
        if (first == upper.Length) return upper;
        var result = new StringBuilder(upper.Length + 12);
        result.Append(upper.AsSpan(0, first));
        for (int i = first; i < upper.Length;)
        {
            int start = i;
            if (char.IsDigit(upper[i]))
            {
                while (i < upper.Length && char.IsDigit(upper[i])) i++;
                result.Append('0', Math.Max(0, 12 - (i - start)));
            }
            else i++;
            result.Append(upper.AsSpan(start, i - start));
        }
        return result.ToString();
    }
}





