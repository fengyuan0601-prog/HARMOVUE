// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

namespace LocalMedia;

public sealed partial class MainWindow
{
    int librarySnapshotRevision = -1;
    List<Media> librarySnapshot = [], sortedMusic = [], sortedVideo = [];
    string[]? musicFilterOptions, videoFilterOptions;
    bool sortedMusicReady, sortedVideoReady;
    List<Media> LibrarySnapshot()
    {
        int revision = store.Revision;
        if (revision == librarySnapshotRevision) return librarySnapshot;
        librarySnapshot = store.Media; librarySnapshotRevision = revision;
        sortedMusic = []; sortedVideo = []; sortedMusicReady = sortedVideoReady = false;
        musicFilterOptions = videoFilterOptions = null;
        return librarySnapshot;
    }
    List<Media> DefaultLibraryView(string kind)
    {
        var snapshot = LibrarySnapshot();
        if (kind == "music" ? sortedMusicReady : sortedVideoReady) return kind == "music" ? sortedMusic : sortedVideo;
        var result = snapshot.Where(m => m.Kind == kind).OrderBy(m => m.NaturalAlbum).ThenBy(m => m.Disc).ThenBy(m => m.Track).ThenBy(m => m.NaturalTitle).ToList();
        if (kind == "music") { sortedMusic = result; sortedMusicReady = true; }
        else { sortedVideo = result; sortedVideoReady = true; }
        return result;
    }
    string[] LibraryFilterOptions(string kind)
    {
        var snapshot = LibrarySnapshot();
        var cached = kind == "music" ? musicFilterOptions : videoFilterOptions;
        if (cached != null) return cached;
        var library = snapshot.Where(m => m.Kind == kind).ToArray();
        IEnumerable<string> options = new[] { "全部" }.Concat(library.Select(m => "文件夹：" + m.Folder).Distinct());
        if (kind == "video") options = options.Concat(new[] { "未看", "未看完", "已看" }).Concat(library.Select(m => "分类：" + m.Category).Distinct());
        cached = options.ToArray();
        if (kind == "music") musicFilterOptions = cached; else videoFilterOptions = cached;
        return cached;
    }
}
