// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

namespace LocalMedia;

public sealed partial class Store
{
    readonly Dictionary<string, int> byId = new(StringComparer.Ordinal);
    readonly Dictionary<string, SortedSet<int>> byKey = new(StringComparer.Ordinal);
    readonly Dictionary<string, SortedSet<int>> byPlainPath = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, SortedSet<int>> byCue = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, SortedSet<int>> byCueDeclaration = new(StringComparer.Ordinal);
    readonly List<(string Key, string Path, string Cue, string Declaration)> indexedRows = [];
    static string DeclarationKey(Media media) => System.IO.Path.GetFullPath(media.CueDeclaredPath.Length == 0 ? media.Path : media.CueDeclaredPath).ToUpperInvariant() + "|" + media.Cue.ToUpperInvariant() + "|" + media.Track;

    public Media? FindById(string id)
    {
        lock (gate)
        {
            EnsureIndexes();
            return byId.TryGetValue(id, out int row) ? items[row] : null;
        }
    }
    bool ContainsRecord(Media media) => byId.TryGetValue(media.Id, out int row) && ReferenceEquals(items[row], media);
    void EnsureIndexes() { if (indexedRows.Count != items.Count) RebuildIndexes(); }
    void RebuildIndexes()
    {
        byId.Clear(); byKey.Clear(); byPlainPath.Clear(); byCue.Clear(); byCueDeclaration.Clear(); indexedRows.Clear();
        for (int row = 0; row < items.Count; row++) AddIndex(row);
    }
    static void AddRow(Dictionary<string, SortedSet<int>> index, string key, int row)
    {
        if (!index.TryGetValue(key, out var rows)) index.Add(key, rows = []);
        rows.Add(row);
    }
    static void RemoveRow(Dictionary<string, SortedSet<int>> index, string key, int row)
    {
        if (!index.TryGetValue(key, out var rows)) return;
        rows.Remove(row);
        if (rows.Count == 0) index.Remove(key);
    }
    void AddIndex(int row)
    {
        var media = items[row]; string key = media.Key;
        byId[media.Id] = row;
        AddRow(byKey, key, row);
        AddRow(media.Cue.Length == 0 ? byPlainPath : byCue, media.Cue.Length == 0 ? media.Path : media.Cue, row);
        string declaration = media.Cue.Length == 0 ? "" : DeclarationKey(media);
        if (declaration.Length != 0) AddRow(byCueDeclaration, declaration, row);
        indexedRows.Add((key, media.Path, media.Cue, declaration));
    }
    void UpdateIndex(int row)
    {
        var before = indexedRows[row]; var media = items[row]; string key = media.Key;
        string declaration = media.Cue.Length == 0 ? "" : DeclarationKey(media);
        if (before.Key == key && before.Path == media.Path && before.Cue == media.Cue && before.Declaration == declaration) return;
        RemoveRow(byKey, before.Key, row);
        RemoveRow(before.Cue.Length == 0 ? byPlainPath : byCue, before.Cue.Length == 0 ? before.Path : before.Cue, row);
        if (before.Declaration.Length != 0) RemoveRow(byCueDeclaration, before.Declaration, row);
        AddRow(byKey, key, row);
        AddRow(media.Cue.Length == 0 ? byPlainPath : byCue, media.Cue.Length == 0 ? media.Path : media.Cue, row);
        if (declaration.Length != 0) AddRow(byCueDeclaration, declaration, row);
        indexedRows[row] = (key, media.Path, media.Cue, declaration);
    }

    // One physical source can own the whole-file record and several CUE
    // tracks. Reconnecting it keeps every ID, queue occurrence and user edit.
    public void RelinkFile(Media selected, string destination)
    {
        destination = System.IO.Path.GetFullPath(destination);
        lock (gate)
        {
            EnsureIndexes();
            if (!ContainsRecord(selected)) throw new InvalidOperationException("这条媒体记录已不在当前资料库中。");
            var changes = items.Where(m => m.Path.Equals(selected.Path, StringComparison.OrdinalIgnoreCase))
                .Select(m => (Media: m, Before: m.Snapshot(), After: m.Snapshot())).ToArray();
            foreach (var change in changes)
            {
                if (change.Before.Cue.Length != 0)
                {
                    string declaration = change.Before.CueDeclaredPath.Length == 0 ? change.Before.Path : change.Before.CueDeclaredPath;
                    change.After.CueDeclaredPath = declaration.Equals(destination, StringComparison.OrdinalIgnoreCase) ? "" : declaration;
                }
                change.After.Path = destination;
            }
            var moved = changes.Select(c => c.Media.Id).ToHashSet(StringComparer.Ordinal);
            var targets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var change in changes)
            {
                string key = change.After.Key;
                if (!targets.Add(key) || byKey.TryGetValue(key, out var rows) && rows.Any(row => !moved.Contains(items[row].Id)))
                    throw new IOException("目标文件已在资料库中，重新定位会产生重复记录；未更改现有资料。");
            }
            try
            {
                foreach (var change in changes) { change.Media.Path = destination; change.Media.CueDeclaredPath = change.After.CueDeclaredPath; }
                SaveCore(changes.Select(c => c.Media));
            }
            catch
            {
                foreach (var change in changes) { change.Media.Path = change.Before.Path; change.Media.CueDeclaredPath = change.Before.CueDeclaredPath; }
                RebuildIndexes();
                throw;
            }
        }
    }
}
