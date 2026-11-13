// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.Globalization;
using System.Text;
using ToolGood.Words.Pinyin;

namespace LocalMedia;

// Immutable UI snapshots keep background indexing away from mutable library objects.
internal sealed record SearchDocument(string Id, string Text)
{
    public static SearchDocument From(Media media) => new(media.Id,
        string.Join('\n', media.Title, media.Artist, media.AlbumArtist, media.Album, media.Category, media.Path));
}

internal sealed class MediaSearch
{
    sealed record Entry(string Source, string Literal, string Full, string Initials);
    readonly Dictionary<string, Entry> cache = [];
    readonly Dictionary<string, (string Full, string Initials)> syllables = [];
    SearchDocument[]? lastDocuments;
    readonly SemaphoreSlim serial = new(1);

    static string Literal(string value) => value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
    static string Compact(string value)
    {
        var output = new StringBuilder(value.Length);
        foreach (char c in value.Normalize(NormalizationForm.FormKD))
            if (char.IsLetterOrDigit(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                output.Append(char.ToLowerInvariant(c));
        return output.ToString();
    }

    (string Full, string Initials) Aliases(string value)
    {
        // Album/artist names and Chinese path segments recur across a library.
        // Transliterate each contiguous Han phrase once; ASCII/numeric parts
        // need no dictionary work. Preserve phrase-level polyphonic handling.
        var full = new StringBuilder(value.Length); var initials = new StringBuilder(value.Length);
        static bool Han(char c) => c is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff';
        for (int i = 0; i < value.Length;)
        {
            int start = i; bool chinese = Han(value[i]);
            while (i < value.Length && Han(value[i]) == chinese) i++;
            string part = value[start..i];
            if (!chinese) { string literal = Compact(part); full.Append(literal); initials.Append(literal); continue; }
            if (!syllables.TryGetValue(part, out var alias))
            {
                alias = (Compact(WordsHelper.GetPinyin(part)), Compact(WordsHelper.GetFirstPinyin(part)));
                if (syllables.Count >= 8192) syllables.Clear();
                syllables[part] = alias;
            }
            full.Append(alias.Full); initials.Append(alias.Initials);
        }
        return (full.ToString(), initials.ToString());
    }

    public async Task<HashSet<string>> FindAsync(SearchDocument[] documents, string query, CancellationToken cancel)
    {
        await serial.WaitAsync(cancel).ConfigureAwait(false);
        try { return await Task.Run(() => Find(documents, query, cancel), cancel).ConfigureAwait(false); }
        finally { serial.Release(); }
    }

    HashSet<string> Find(SearchDocument[] documents, string query, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        string literal = Literal(query.Trim()), compact = Compact(query);
        bool roman = compact.Length > 0 && compact.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9');
        HashSet<string> result = [];
        HashSet<string>? live = ReferenceEquals(lastDocuments, documents) ? null : [];
        // A Chinese or other non-Roman query can only match literal text. Do
        // not load the pinyin dictionary or build aliases for the entire library
        // before returning those matches; a later Roman query builds them lazily.
        if (!roman)
        {
            foreach (var document in documents)
            {
                cancel.ThrowIfCancellationRequested();
                if (Literal(document.Text).Contains(literal, StringComparison.Ordinal)) result.Add(document.Id);
            }
            return result;
        }
        foreach (var document in documents)
        {
            cancel.ThrowIfCancellationRequested();
            live?.Add(document.Id);
            if (!cache.TryGetValue(document.Id, out var entry) || entry.Source != document.Text)
            {
                // Field separators remain in aliases: never match the end of one field to another.
                string[] fields = document.Text.Split('\n');
                var aliases = fields.Select(Aliases).ToArray();
                entry = new(document.Text, Literal(document.Text),
                    string.Join('\n', aliases.Select(a => a.Full)),
                    string.Join('\n', aliases.Select(a => a.Initials)));
                cache[document.Id] = entry;
            }
            if (entry.Literal.Contains(literal, StringComparison.Ordinal) ||
                roman && (entry.Full.Contains(compact, StringComparison.Ordinal) || entry.Initials.Contains(compact, StringComparison.Ordinal)))
                result.Add(document.Id);
        }
        if (live != null)
            foreach (string id in cache.Keys.Where(id => !live.Contains(id)).ToArray()) cache.Remove(id);
        lastDocuments = documents;
        return result;
    }
}
