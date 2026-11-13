// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

namespace LocalMedia;

public sealed partial class MainWindow
{
    void BuildVideoShelf()
    {
        string key = ShelfKey(videoLibraryMode);
        if (videoShelfKey != key) { videoShelfKey = key; videoShelfPage = 0; }
        videoShelfPage = Math.Clamp(videoShelfPage, 0, Math.Max(0, (visible.Count - 1) / 60));
        var shelf = AcquireShelf("video", ShelfSource(key, videoShelfPage), true, out bool unchanged);
        if (unchanged) { ShowShelf(shelf); return; }
        var entries = visible.Skip(videoShelfPage * 60).Take(60).ToArray();
        shelf.SetItems(entries.Select(m => new VirtualShelfItem(m.Id, m.Title, m.Watch + " · " + DurationText(m.Duration), m, UiTheme.Video, m.Duration > 0 ? m.Position / m.Duration : 0)).ToArray());
        shelf.CoverLoader = (item, cancel) => LoadShelfCover(shelf, item, (Media)item.Tag!, cancel);
        void Open(VirtualShelfItem item) { var media = (Media)item.Tag!; Play(media, media.Position > 0); }
        shelf.ItemDoubleClick = Open;
        shelf.ItemActivate = (item, _) => Open(item);
        shelf.ItemContextMenu = (item, point) => { var menu = Menu((Media)item.Tag!); UiMenus.DisposeAfterClose(menu, this); menu.Show(shelf, point); };
        SetShelfFooter(shelf, ShelfPager(shelf, visible.Count, videoShelfPage, value => { videoShelfPage = value; Render(); }));

        ShowShelf(shelf);
    }
}