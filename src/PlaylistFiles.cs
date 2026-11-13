// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.Globalization;
using System.Text;

namespace LocalMedia;

public static class PlaylistFiles
{
    public sealed record ImportResult(List<MediaList> Lists, int Offline, int Skipped);

    // A local playlist grants access to its listed files; network locations are never fetched.
    public static ImportResult Import(Store store, string file)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(file))!;
        var music = new MediaList { Name = Path.GetFileNameWithoutExtension(file) };
        var video = new MediaList { Name = music.Name, Video = true };
        var physical = store.Media.Where(m => m.Cue == "").GroupBy(m => m.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var pending = new List<Media>();
        int offline = 0, skipped = 0;
        foreach (string line in Scanner.Text(file).Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0 && !s.StartsWith('#')))
        {
            try
            {
                string value = line;
                if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !Path.IsPathRooted(value))
                {
                    if (!uri.IsFile) { skipped++; continue; }
                    value = uri.LocalPath;
                }
                else if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) value = new Uri(value).LocalPath;
                string path = Path.GetFullPath(value, directory);
                if (!Scanner.Extensions.Contains(Path.GetExtension(path))) { skipped++; continue; }
                // Prefer the physical-file record; M3U cannot identify a CUE virtual boundary.
                if (!physical.TryGetValue(path, out var media))
                {
                    media = File.Exists(path) ? Scanner.Read(path) : new Media { Path = path, Title = Path.GetFileNameWithoutExtension(path), Kind = VideoExtensions.Contains(Path.GetExtension(path)) ? "video" : "music" };
                    physical[path] = media; pending.Add(media);
                }
                if (!media.Online) offline++;
                (media.Kind == "video" ? video : music).Items.Add(media.Id);
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException or UriFormatException) { skipped++; }
        }
        var lists = new[] { music, video }.Where(l => l.Items.Count > 0).ToList();
        if (lists.Count == 2) { music.Name += " · 音乐"; video.Name += " · 视频"; }
        store.Upsert(pending); store.State.Lists.AddRange(lists); store.Save();
        return new(lists, offline, skipped);
    }
    static readonly HashSet<string> VideoExtensions = new(".mp4 .mkv .mov .avi .webm .flv .wmv .asf .mpg .mpeg .ts .m2ts .rm .rmvb .m4v".Split(' '), StringComparer.OrdinalIgnoreCase);

    public static void Export(Store store, MediaList list, string file)
    {
        var index = store.Media.ToDictionary(m => m.Id);
        var lines = new List<string> { "#EXTM3U", "# HARMOVUE: CUE virtual boundaries are not represented in M3U8; use a database backup for complete data." };
        foreach (string id in list.Items)
            if (index.TryGetValue(id, out var media))
            {
                string title = media.Title.Replace('\r', ' ').Replace('\n', ' ');
                lines.Add("#EXTINF:" + ((int)media.Duration).ToString(CultureInfo.InvariantCulture) + "," + title);
                lines.Add(media.Path);
            }
        File.WriteAllLines(file, lines, new UTF8Encoding(false));
    }
}
