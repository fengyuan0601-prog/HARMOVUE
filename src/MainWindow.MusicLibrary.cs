// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

namespace LocalMedia;

public sealed partial class MainWindow
{
    readonly Panel nowRail = new GlassCanvas() { Dock = DockStyle.Right, Width = 320, Visible = false };
    readonly PictureBox railCover = new CoverPictureBox { Visible = false };
    readonly UiListBox railQueue = new() { BackColor = UiTheme.Surface };
    Label railHeading = null!, railEmpty = null!;
    UiButton railQueueClose = null!, railQueueOpen = null!;
    List<Media>? albumPreview;
    string railQueueKey = "";
    int railWindowStart = -1;
    bool queueDrawerOpen;
    UiMotion? queueMotion, headingMotion;
    bool queueAnimating;
    int headingOffset;
    double queueReveal;
    bool railQueueDirty = true;
    Action? layoutAlbumShelf;
    readonly List<int> railQueueIndices = [];

    void BuildNowRail()
    {
        railHeading = Caption("播放队列", 13, bold: true);
        railEmpty = Caption("队列为空", 10, UiTheme.Muted);
        railQueueOpen = (UiButton)Button("查看全部", () => { queueDrawerOpen = false; Navigate("播放队列"); }, 104, 36);
        railQueueOpen.Style = UiButtonStyle.Quiet;
        railQueueClose = IconButton("收起播放队列", "\uE711", ToggleQueueDrawer);
        railQueue.DoubleClick += (_, _) => { if (railQueue.SelectedIndex >= 0 && railQueue.SelectedIndex < railQueueIndices.Count) SelectQueue(railQueueIndices[railQueue.SelectedIndex]); };
        railQueue.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && railQueue.SelectedIndex >= 0 && railQueue.SelectedIndex < railQueueIndices.Count) { SelectQueue(railQueueIndices[railQueue.SelectedIndex]); e.Handled = true; } };
        nowRail.Controls.AddRange([railHeading, railCover, railQueueOpen, railQueueClose, railQueue, railEmpty]);
        nowRail.Resize += (_, _) => LayoutNowRail(); LayoutNowRail();
    }
    void ToggleQueueDrawer()
    {
        using var measurement = UiPerformance.Measure("queue_toggle_prepare");
        queueDrawerOpen = !queueDrawerOpen;
        railQueueDirty = true;
        // Prepare the bounded preview before starting the animation clock.
        UpdateNowRail();
        queueMotion ??= new UiMotion(this, "queue_motion");
        double from = nowRail.Visible ? queueReveal : 0;
        double target = queueDrawerOpen ? 1 : 0;
        queueAnimating = true;
        // Arrange the final page once. Changing the panel width on every frame
        // repeatedly resized the table, rebuilt backgrounds and delayed ticks.
        SuspendLayout(); main.SuspendLayout(); nowRail.SuspendLayout();
        try { LayoutChrome(); }
        finally { nowRail.ResumeLayout(true); main.ResumeLayout(true); ResumeLayout(true); }
        layoutAlbumShelf?.Invoke();
        queueMotion.Run(170, progress => SetQueueReveal(from + (target - from) * progress),
            () => { queueAnimating = false; LayoutChrome(); ClearQueueRegion(); if (queueDrawerOpen) railQueue.Focus(); });
    }
    void SetQueueReveal(double value)
    {
        queueReveal = Math.Clamp(value, 0, 1);
        int width = (int)Math.Round(nowRail.Width * queueReveal);
        var previous = nowRail.Region;
        nowRail.Region = new Region(new Rectangle(nowRail.Width - width, 0, width, nowRail.Height));
        previous?.Dispose();
    }
    void ClearQueueRegion() { var previous = nowRail.Region; nowRail.Region = null; previous?.Dispose(); }
    void StopQueueMotion() { queueMotion?.Stop(); queueAnimating = false; queueReveal = queueDrawerOpen ? 1 : 0; ClearQueueRegion(); }
    void AnimateHeading()
    {
        headingMotion ??= new UiMotion(pageHeading, "heading_motion");
        headingMotion.Run(140, progress =>
        {
            headingOffset = (int)Math.Round(5 * dpiFactor * (1 - progress));
            pageHeading.Top = (int)(20 * dpiFactor) + headingOffset;
            pageHeading.ForeColor = Color.FromArgb((int)(150 + 105 * progress), UiTheme.Ink);
        });
    }
    void LayoutNowRail()
    {
        if (railHeading == null) return;
        float scale = dpiReady ? dpiFactor : 1;
        int P(float value) => (int)(value * scale);
        int width = nowRail.ClientSize.Width, inset = P(24), bodyWidth = Math.Max(P(100), width - inset * 2);
        railHeading.SetBounds(inset, P(28), bodyWidth - P(38), P(30));
        railQueueClose.SetBounds(width - inset - P(32), P(26), P(32), P(32));
        railQueue.SetBounds(inset - P(6), P(86), bodyWidth + P(12), Math.Max(P(60), nowRail.Height - P(154)));
        railEmpty.SetBounds(inset, P(96), bodyWidth, P(28));
        railQueueOpen.SetBounds(inset - P(12), Math.Max(P(152), nowRail.Height - P(54)), P(108), P(36));
    }
    Media? RailMedia() => page == "音乐库" && albumPreview?.Count > 0 ? albumPreview[0] : player?.Current;
    void SelectAlbum(List<Media> items)
    {
        albumPreview = items;
    }
    void PlayRailSelection()
    {
        var media = RailMedia();
        if (media == null) { AddRoot(); return; }
        if (media.Kind == "video") { ResumeSession("video", true); return; }
        var items = albumPreview ?? store.Media.Where(item => item.Kind == "music" && AlbumKey(item) == AlbumKey(media)).OrderBy(item => item.Disc).ThenBy(item => item.Track).ThenBy(item => item.NaturalTitle).ToList();
        if (items.Count == 0) return;
        albumPreview = null; visible = items;
        _ = PlayWithFeedback(items.Cast<Media?>().ToArray(), 0, false);
        page = "音乐库"; group = "全部"; libraryMode = "专辑"; Render();
    }
    void UpdateNowRail()
    {
        if (railHeading == null || player == null) return;
        if (!queueDrawerOpen) return;
        using var measurement = UiPerformance.Measure("queue_drawer_update", count: player.Session.Queue.Count);
        var session = player.Session;
        const int previewSize = 80;
        int start = Math.Clamp(session.Index - 20, 0, Math.Max(0, session.Queue.Count - previewSize));
        bool dirty = railQueueDirty || start != railWindowStart;
        string queueKey = dirty ? start + ":" + string.Join('|', session.Queue.Skip(start).Take(previewSize)) : railQueueKey;
        railQueueDirty = false;
        if (dirty || queueKey != railQueueKey)
        {
            railQueue.BeginUpdate();
            try
            {
                railQueue.Items.Clear(); railQueueIndices.Clear();
                for (int index = start; index < Math.Min(session.Queue.Count, start + previewSize); index++)
                    if (store.FindById(session.Queue[index]) is { } item)
                    {
                        railQueueIndices.Add(index);
                        railQueue.Items.Add(new MediaRow(item, (item.Artist.Length > 0 ? item.Artist + " · " : "") + DurationText(item.Duration), index + 1));
                    }
                railQueueKey = queueKey; railWindowStart = start;
            }
            catch { railQueueDirty = true; throw; }
            finally { railQueue.EndUpdate(); }
        }
        railHeading.Text = "播放队列" + (session.Queue.Count > 0 ? "  ·  " + session.Queue.Count : "");
        railEmpty.Visible = railQueue.Items.Count == 0;
        int selected = railQueueIndices.IndexOf(session.Index);
        if (railQueue.SelectedIndex != selected) railQueue.SelectedIndex = selected;
    }
    static string AlbumName(Media media) => string.IsNullOrWhiteSpace(media.Album) ? Path.GetFileName(media.Folder.TrimEnd(Path.DirectorySeparatorChar)) : media.Album;
    sealed record ShelfAlbum(string Key, List<Media> Items);
    void AlbumShelf(string mode)
    {
        string key = ShelfKey(mode);
        if (musicShelfKey != key) { musicShelfKey = key; musicShelfPage = 0; }
        var shelf = AcquireShelf("music-" + mode, ShelfSource(key, musicShelfPage), false, out bool unchanged);
        if (unchanged) { ShowShelf(shelf); return; }
        var allGroups = visible.GroupBy(media => mode == "专辑" ? AlbumKey(media) : media.Artist).OrderBy(entry => Scanner.Natural(entry.Key)).ToList();
        musicShelfPage = Math.Clamp(musicShelfPage, 0, Math.Max(0, (allGroups.Count - 1) / 60));
        var entries = new List<VirtualShelfItem>();
        foreach (var entry in allGroups.Skip(musicShelfPage * 60).Take(60))
        {
            var items = entry.OrderBy(item => item.Disc).ThenBy(item => item.Track).ThenBy(item => item.NaturalTitle).ToList();
            string name = mode == "专辑" ? AlbumName(items[0]) : string.IsNullOrWhiteSpace(entry.Key) ? "未知歌手" : entry.Key;
            string artist = mode == "歌手" ? items.Count + " 首歌曲" : items[0].AlbumArtist.Length > 0 ? items[0].AlbumArtist : items[0].Artist.Length > 0 ? items[0].Artist : "未知歌手";
            entries.Add(new VirtualShelfItem(entry.Key, name, artist, new ShelfAlbum(entry.Key, items), UiTheme.Music));
        }
        shelf.SetItems(entries);
        shelf.CoverLoader = (item, cancel) => LoadShelfCover(shelf, item, ((ShelfAlbum)item.Tag!).Items[0], cancel);
        void Open(VirtualShelfItem item) { var album = (ShelfAlbum)item.Tag!; albumPreview = null; group = (mode == "专辑" ? "专辑ID：" : "歌手：") + album.Key; Render(); }
        void PlayAlbum(VirtualShelfItem item) { SelectAlbum(((ShelfAlbum)item.Tag!).Items); PlayRailSelection(); }
        shelf.ItemClick = Open;
        shelf.ItemActivate = (item, keys) => { if ((keys & Keys.Control) != 0) PlayAlbum(item); else Open(item); };
        shelf.ItemContextMenu = (item, point) =>
        {
            var menu = new ContextMenuStrip { BackColor = UiTheme.Surface, ForeColor = UiTheme.Ink };
            menu.Items.Add("查看曲目", null, (_, _) => Open(item)); menu.Items.Add("播放专辑", null, (_, _) => PlayAlbum(item));
            UiMenus.DisposeAfterClose(menu, this); menu.Show(shelf, point);
        };
        SetShelfFooter(shelf, ShelfPager(shelf, allGroups.Count, musicShelfPage, value => { musicShelfPage = value; Render(); }));

        ShowShelf(shelf);
        if (entries.Count == 0)
        {
            var arrangeEmpty = AddLibraryEmptyState(shelf, "把喜欢的音乐，留在这里");
            layoutAlbumShelf = () => { shelf.ArrangeShelf(); arrangeEmpty(); };
        }
    }
}
