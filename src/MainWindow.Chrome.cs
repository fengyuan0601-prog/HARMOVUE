// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

namespace LocalMedia;

public sealed partial class MainWindow
{
    readonly List<UiButton> navigationButtons = [];
    readonly ToolTip tips = new() { InitialDelay = 450, ReshowDelay = 100, AutoPopDelay = 8000 };
    readonly GlassCanvas playerBar = new() { Dock = DockStyle.Bottom, Height = 88, BackColor = UiTheme.Sidebar, Floating = true };
    readonly SurfacePanel searchShell = new() { Width = 220, Height = 36, Radius = 18, ShowBorder = false, SolidSurface = true, BackColor = UiTheme.Surface };
    readonly PictureBox footerCover = new CoverPictureBox { SizeMode = PictureBoxSizeMode.Zoom, BackColor = UiTheme.Surface, Cursor = Cursors.Hand };
    readonly Label footerSubtitle = new GlassLabel() { ForeColor = UiTheme.Muted, AutoEllipsis = true };
    readonly Label footerOutput = new GlassLabel() { ForeColor = UiTheme.Muted, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleRight };
    UiButton transportPlay = null!, transportPrevious = null!, transportNext = null!, queueShortcut = null!, volumeShortcut = null!;
    string? footerMediaId;
    readonly Label pageHeading = new GlassLabel() { AutoEllipsis = true };
    readonly Label pageDescription = new GlassLabel() { AutoEllipsis = true, ForeColor = UiTheme.Muted };
    UiButton addMediaButton = null!, shuffleButton = null!, repeatButton = null!, transportMore = null!;
    int openPlaybackMenus;
    bool searchExpanded;


    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UiTheme.WindowChrome(this, !immersiveVideo); }

    void Navigate(string destination)
    {
        if (immersiveVideo) ExitVideoPresentation();
        if (destination != "音乐库") albumPreview = null;
        page = destination; group = "全部"; selectedList = null;
        if (destination == "视频库") RequestGpuWarmup();
        Render();
    }
    void ResumeSession(string kind, bool showPage = false) => _ = ResumeWithFeedback(kind, showPage: showPage);
    Label Caption(string text, float size = 10, Color? color = null, bool bold = false) => new GlassLabel()
    {
        Text = text, ForeColor = color ?? UiTheme.Ink, Font = OwnedFont(size, bold ? FontStyle.Bold : FontStyle.Regular), AutoEllipsis = true, BackColor = Color.Transparent
    };
    UiButton IconButton(string label, string glyph, Action action, bool primary = false)
    {
        var button = new UiButton { Text = label, AccessibleName = label, IconOnly = true, Glyph = glyph, Style = primary ? UiButtonStyle.Circle : UiButtonStyle.Quiet, Width = 36, Height = 36, Margin = new Padding(0) };
        button.Click += (_, _) => Try(action); tips.SetToolTip(button, label); return button;
    }
    void BuildChrome()
    {
        main.BackColor = BackColor = UiTheme.Background; ForeColor = UiTheme.Ink;
        nav.BackColor = UiTheme.Sidebar;
        // The rail is one material surface; avoid opaque layout hosts over it.
        var rail = (GlassCanvas)nav;
        rail.Material = GlassMaterial.Rail;
        rail.CornerRadius = 26;
        rail.SurfaceInset = new Padding(10, 14, 0, 14);
        void Route(string route, string label, string glyph)
        {
            var button = new UiButton { Text = label, Glyph = glyph, IconOnly = false, Style = UiButtonStyle.Navigation, Width = 64, Height = 66, Margin = Padding.Empty, AccessibleName = route, Font = OwnedFont(8.5f) };
            button.Click += (_, _) => Navigate(route); tips.SetToolTip(button, route); navigationButtons.Add(button); nav.Controls.Add(button);
        }
        Route("音乐库", "音乐", UiTheme.Music); Route("视频库", "视频", UiTheme.Video);
        Route("歌单与集合", "歌单", UiTheme.Library); Route("收藏", "收藏", UiTheme.Favorite);
        Route("设置", "设置", UiTheme.Settings);
        nav.Resize += (_, _) => LayoutNavigation();

        toolbar.BackColor = UiTheme.Background;
        search.BorderStyle = BorderStyle.None; search.BackColor = UiTheme.Surface; search.ForeColor = UiTheme.Ink; search.PlaceholderText = "搜索"; search.AccessibleName = "搜索音乐与视频";
        tips.SetToolTip(search, "搜索音乐、视频或文件 · 支持拼音 · Ctrl+F");
        searchShell.Controls.Add(search);
        searchShell.Cursor = Cursors.Hand;
        searchShell.Click += (_, _) => { searchExpanded = true; LayoutChrome(); search.Focus(); };
        search.LostFocus += (_, _) => { if (search.Text.Length == 0) { searchExpanded = false; LayoutChrome(); } };
        searchShell.Paint += (_, e) => UiTheme.Glyph(e.Graphics, UiTheme.Search, new Rectangle((int)(11 * DeviceDpi / 96f), 0, (int)(24 * DeviceDpi / 96f), searchShell.Height), UiTheme.Muted, 11);
        searchShell.Resize += (_, _) => LayoutSearch();
        pageHeading.Font = OwnedFont(24); pageHeading.ForeColor = UiTheme.Ink;
        pageDescription.Font = OwnedFont(9);
        toolbar.Controls.Add(searchShell); toolbar.Controls.Add(pageHeading); toolbar.Controls.Add(pageDescription);
        addMediaButton = new UiButton { Text = "导入", Glyph = UiTheme.Add, AccessibleName = "打开文件或添加媒体目录", Style = UiButtonStyle.Secondary, Font = OwnedFont(9.5f), Width = 88, Height = 40 };
        addMediaButton.Click += (_, _) => ShowAddMediaMenu(); toolbar.Controls.Add(addMediaButton);
        toolbar.Resize += (_, _) => LayoutHeader();
        status.ForeColor = UiTheme.Muted; status.Font = OwnedFont(8); status.Height = 20; status.Padding = new Padding(44, 0, 20, 0); status.Visible = false;
        status.TextChanged += (_, _) => { messageUntil = DateTime.Now.AddSeconds(8); status.Visible = !string.IsNullOrEmpty(status.Text); };
        main.Controls.Add(content); main.Controls.Add(toolbar); main.Controls.Add(status);
        BuildNowRail();
        Controls.Add(main); Controls.Add(nav); Controls.Add(nowRail); Controls.Add(playerBar);

        footerCover.Paint += (_, e) => { if (footerCover.Image == null) UiTheme.Glyph(e.Graphics, player?.Current?.Kind == "video" ? UiTheme.Video : UiTheme.Music, footerCover.ClientRectangle, UiTheme.Muted, 18); };
        footerCover.Click += (_, _) => Navigate("正在播放");
        now.AutoEllipsis = true; now.Height = 24; now.Text = "尚未播放"; now.Font = OwnedFont(10.5f);
        now.Cursor = Cursors.Hand; now.Click += (_, _) => Navigate("正在播放");
        footerSubtitle.Font = OwnedFont(8.5f); footerSubtitle.Text = "";
        footerOutput.Font = OwnedFont(8);
        clock.Font = OwnedFont(8.5f); clock.ForeColor = UiTheme.Muted;
        seek.BackColor = volume.BackColor = playerBar.BackColor; seek.AccessibleName = "播放进度"; volume.AccessibleName = "音量";
        transportPrevious = IconButton("上一首", UiTheme.Previous, () => Transport(() => player.Next(true)));
        transportPlay = IconButton("播放 / 暂停", UiTheme.Play, TogglePlayback, true);
        transportNext = IconButton("下一首", UiTheme.Next, () => Transport(() => player.Next()));
        queueShortcut = IconButton("播放队列", UiTheme.Queue, () => { if (immersiveVideo) Navigate("播放队列"); else ToggleQueueDrawer(); });
        shuffleButton = IconButton("随机播放", "\uE8B1", () => { store.State.Shuffle = !store.State.Shuffle; store.SaveState(); UpdatePlayerChrome(); });
        repeatButton = IconButton("循环模式", "\uE8EE", () => { store.State.Repeat = store.State.Repeat switch { "off" => "all", "all" => "one", _ => "off" }; player.ApplyRepeatPolicy(); store.SaveState(); UpdatePlayerChrome(); });
        volumeShortcut = IconButton("音量与输出设备", UiTheme.Volume, ShowVolumeMenu);
        transportMore = IconButton("更多播放选项", UiTheme.More, ShowMusicPlaybackMenu);
        playerBar.Controls.AddRange([footerCover, now, footerSubtitle, transportPrevious, transportPlay, transportNext, seek, clock, volume, queueShortcut, volumeShortcut, footerOutput, shuffleButton, repeatButton, transportMore]);
        playerBar.Resize += (_, _) => LayoutPlayerBar();
        volume.Value = store.State.Volume;
        volume.ValueChanged += (_, _) => Try(() => { store.State.Volume = volume.Value; player.SetEngineOption("volume", volume.Value); });
        Disposed += (_, _) => { tips.Dispose(); footerCover.Image?.Dispose(); railCover.Image?.Dispose(); };
        LayoutHeader(); LayoutSearch(); LayoutPlayerBar();
    }
    void LayoutSearch()
    {
        float s = dpiReady ? dpiFactor : 1;
        search.SetBounds((int)(42 * s), Math.Max(1, (searchShell.Height - search.PreferredHeight) / 2), Math.Max(30, searchShell.Width - (int)(56 * s)), search.PreferredHeight);
    }
    void LayoutHeader()
    {
        if (addMediaButton == null) return;
        float s = dpiReady ? dpiFactor : 1;
        int P(float n) => (int)(n * s);
        int width = toolbar.ClientSize.Width;
        searchShell.SetBounds(Math.Max(P(186), width - searchShell.Width - P(132)), P(28), searchShell.Width, P(40));
        addMediaButton.SetBounds(width - P(120), P(28), P(88), P(40));
        pageHeading.SetBounds(P(32), P(20) + headingOffset, Math.Max(P(120), searchShell.Left - P(48)), P(41));
        pageDescription.SetBounds(P(33), P(65), Math.Max(P(120), searchShell.Left - P(48)), P(21));
        LayoutSearch();
    }
    void LayoutNavigation()
    {
        float scale = dpiReady ? dpiFactor : 1;
        int P(float n) => (int)(n * scale);
        for (int index = 0; index < navigationButtons.Count; index++)
        {
            int top = index == 4 ? Math.Max(P(356), nav.ClientSize.Height - P(94)) : P(32 + index * 76 + (index >= 2 ? 16 : 0));
            navigationButtons[index].SetBounds(P(21), top, P(64), P(66));
        }
    }
    void LayoutChrome()
    {
        if (changingVideoPresentation) return;
        float s = dpiReady ? dpiFactor : 1;
        int P(float value) => (int)(value * s);
        bool compact = ClientSize.Width < P(1180);
        nav.Width = P(96);
        toolbar.Height = P(104);
        LayoutNavigation();
        nowRail.Width = P(300);
        nowRail.Visible = (queueDrawerOpen || queueAnimating) && !immersiveVideo && !full;
        queueShortcut.Selected = queueDrawerOpen;
        bool searching = searchExpanded || search.Text.Length > 0 || main.ClientSize.Width >= P(760);
        search.Visible = searching;
        searchShell.Width = P(searching ? main.ClientSize.Width < P(640) ? 170 : 228 : 44);
        playerBar.Floating = !immersiveVideo;
        if (!immersiveVideo) playerBar.Height = P(88);
        LayoutHeader(); LayoutPlayerBar(); LayoutNowRail();
    }
    void ShowAddMediaMenu()
    {
        var menu = new ContextMenuStrip { BackColor = UiTheme.Surface, ForeColor = UiTheme.Ink };
        menu.Items.Add("打开影音文件…", null, (_, _) => OpenFiles());
        menu.Items.Add("添加媒体目录…", null, (_, _) => AddRoot());
        menu.Items.Add("导入 M3U / M3U8…", null, (_, _) => ImportList());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("继续听", null, (_, _) => ResumeSession("music")); menu.Items.Add("继续看", null, (_, _) => ResumeSession("video", true));
        menu.Items.Add("最近播放与添加", null, (_, _) => Navigate("首页"));
        UiMenus.DisposeAfterClose(menu, this); menu.Show(addMediaButton, new Point(0, addMediaButton.Height));
    }
    void LayoutPlayerBar()
    {
        if (changingVideoPresentation || transportPlay == null) return;
        float s = dpiReady ? dpiFactor : 1; int w = playerBar.ClientSize.Width;
        int P(float value) => (int)(value * s);
        if (!immersiveVideo)
        {
            int middle = w / 2;
            footerCover.SetBounds(P(30), P(27), P(40), P(40));
            now.SetBounds(P(84), P(27), Math.Max(P(100), middle - P(200)), P(22));
            footerSubtitle.SetBounds(P(84), P(50), Math.Max(P(100), middle - P(200)), P(18));
            transportPlay.SetBounds(middle - P(20), P(27), P(40), P(40));
            transportPrevious.SetBounds(middle - P(68), P(30), P(34), P(34));
            transportNext.SetBounds(middle + P(34), P(30), P(34), P(34));
            seek.SetBounds(P(40), P(12), w - P(80), P(12));
            clock.SetBounds(w - P(250), P(35), P(108), P(22));
            volumeShortcut.SetBounds(w - P(138), P(30), P(32), P(34));
            queueShortcut.SetBounds(w - P(96), P(30), P(32), P(34));
            transportMore.SetBounds(w - P(54), P(30), P(32), P(34));
            volume.Visible = shuffleButton.Visible = repeatButton.Visible = footerOutput.Visible = false;
            transportMore.Visible = true;
            return;
        }
        transportMore.Visible = false; volume.Visible = true;
        bool compact = w < P(1080); int center = w / 2;
        int cover = P(60), textLeft = P(26) + cover + P(16);
        footerCover.SetBounds(P(26), P(25), cover, cover);
        int titleWidth = Math.Max(P(100), center - P(compact ? 210 : 302) - textLeft);
        now.SetBounds(textLeft, P(33), titleWidth, P(24)); footerSubtitle.SetBounds(textLeft, P(59), titleWidth, P(22));
        int playSize = P(compact ? 44 : 54);
        transportPlay.SetBounds(center - playSize / 2, P(9), playSize, playSize); transportPrevious.SetBounds(center - P(80), P(compact ? 14 : 19), P(34), P(34)); transportNext.SetBounds(center + P(46), P(compact ? 14 : 19), P(34), P(34));
        shuffleButton.Visible = repeatButton.Visible = !compact;
        shuffleButton.SetBounds(center - P(132), P(19), P(32), P(34)); repeatButton.SetBounds(center + P(100), P(19), P(32), P(34));
        int timelineWidth = P(compact ? 300 : 560);
        seek.SetBounds(center - timelineWidth / 2, P(70), timelineWidth, P(24)); clock.SetBounds(center + timelineWidth / 2 + P(8), P(71), P(110), P(22));
        queueShortcut.SetBounds(w - P(54), P(38), P(34), P(34)); volume.SetBounds(w - P(174), P(44), P(100), P(24)); volumeShortcut.SetBounds(w - P(214), P(38), P(32), P(34));
        footerOutput.Visible = false;
    }
    void ShowVolumeMenu()
    {
        var menu = new ContextMenuStrip { BackColor = UiTheme.Surface, ForeColor = UiTheme.Ink };
        var slider = new TimelineSlider { Width = (int)(170 * DeviceDpi / 96f), Height = (int)(34 * DeviceDpi / 96f), BackColor = UiTheme.Surface, Minimum = 0, Maximum = 100, Value = volume.Value, AccessibleName = "音量" };
        var label = new ToolStripMenuItem($"音量 {volume.Value}%") { Enabled = false };
        slider.ValueChanged += (_, _) => { volume.Value = slider.Value; label.Text = $"音量 {slider.Value}%"; };
        menu.Items.Add(label); menu.Items.Add(new ToolStripControlHost(slider));
        menu.Items.Add("选择输出设备…", null, (_, _) => ChooseDevice());
        openPlaybackMenus++;
        UiMenus.DisposeAfterClose(menu, this);
        menu.Closed += (_, _) => { openPlaybackMenus--; videoInteraction = DateTime.UtcNow; store.SaveState(); };
        menu.Show(volumeShortcut, new Point(0, 0), ToolStripDropDownDirection.AboveLeft);
    }
    void ShowMusicPlaybackMenu()
    {
        var menu = new ContextMenuStrip { BackColor = UiTheme.Surface, ForeColor = UiTheme.Ink };
        menu.Items.Add(new ToolStripMenuItem("随机播放", null, (_, _) => { store.State.Shuffle = !store.State.Shuffle; store.SaveState(); UpdatePlayerChrome(); }) { Checked = store.State.Shuffle });
        var repeat = new ToolStripMenuItem("循环播放");
        foreach (var option in new[] { ("off", "关闭"), ("all", "列表循环"), ("one", "单曲循环") })
        {
            var value = option.Item1;
            repeat.DropDownItems.Add(new ToolStripMenuItem(option.Item2, null, (_, _) => Try(() => {
                store.State.Repeat = value; player.ApplyRepeatPolicy(); store.SaveState(); UpdatePlayerChrome();
            })) { Checked = store.State.Repeat == value });
        }
        menu.Items.Add(repeat);
        menu.Items.Add("正在播放与歌词", null, (_, _) => Navigate("正在播放"));
        menu.Items.Add("查看完整队列", null, (_, _) => Navigate("播放队列"));
        menu.Items.Add("继续听", null, (_, _) => ResumeSession("music"));
        menu.Items.Add("继续看", null, (_, _) => ResumeSession("video", true));
        UiMenus.DisposeAfterClose(menu, this);
        menu.Show(transportMore, new Point(0, 0), ToolStripDropDownDirection.AboveLeft);
    }
    void UpdatePlayerChrome()
    {
        var media = player.Current;
        playerBar.Visible = media != null;
        now.Text = media?.Title ?? "尚未播放";
        footerSubtitle.Text = media?.Artist ?? "";
        if (transportPlay != null)
        {
            string glyph = player.Paused ? UiTheme.Play : UiTheme.Pause;
            if (transportPlay.Glyph != glyph) { transportPlay.Glyph = glyph; transportPlay.Invalidate(); }
            shuffleButton.Selected = store.State.Shuffle; repeatButton.Selected = store.State.Repeat != "off";
            tips.SetToolTip(repeatButton, "循环：" + (store.State.Repeat == "one" ? "单曲" : store.State.Repeat == "all" ? "列表" : "关闭"));
        }
        if (media?.Id != footerMediaId)
        {
            footerMediaId = media?.Id; footerCover.Image?.Dispose(); footerCover.Image = null;
            if (media != null) LoadCover(media, footerCover);
            footerCover.Invalidate();
        }
        if (media != null && footerOutput.Visible) footerOutput.Text = OutputDescription();
        UpdateNowRail();
    }
    LibraryChromeEnvironment? appliedGridTheme;
    void ThemeGrid(DataGridView table, GridFileAvailability availability, bool initialize = true)
    {
        string[] headings = page == "音乐库" ? ["歌曲", "歌手", "专辑", "时长", "状态"] : ["标题", "分类 / 歌手", "文件夹 / 专辑", "时长", "状态"];
        var environment = CurrentLibraryChromeEnvironment();
        if (!initialize && appliedGridTheme == environment)
        {
            for (int i = 0; i < headings.Length; i++) if (table.Columns[i].HeaderText != headings[i]) table.Columns[i].HeaderText = headings[i];
            return;
        }
        appliedGridTheme = environment;
        table.BackgroundColor = UiTheme.Surface; table.GridColor = UiTheme.Surface;
        table.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal; table.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        table.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing; table.ColumnHeadersHeight = (int)(42 * DeviceDpi / 96f);
        table.DefaultCellStyle.BackColor = UiTheme.Surface; table.AlternatingRowsDefaultCellStyle.BackColor = UiTheme.Surface;
        table.DefaultCellStyle.ForeColor = UiTheme.Ink; table.DefaultCellStyle.SelectionBackColor = UiTheme.AccentSoft; table.DefaultCellStyle.SelectionForeColor = UiTheme.Ink;
        table.DefaultCellStyle.Padding = new Padding((int)(12 * DeviceDpi / 96f), 0, 4, 0);
        table.ColumnHeadersDefaultCellStyle.BackColor = UiTheme.Surface; table.ColumnHeadersDefaultCellStyle.ForeColor = UiTheme.Muted;
        table.ColumnHeadersDefaultCellStyle.SelectionBackColor = UiTheme.Surface; table.ColumnHeadersDefaultCellStyle.SelectionForeColor = UiTheme.Muted;
        table.ColumnHeadersDefaultCellStyle.Padding = table.DefaultCellStyle.Padding;
        table.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        table.RowTemplate.Height = (int)(55 * DeviceDpi / 96f); table.AllowUserToResizeRows = false;
        table.Columns[0].FillWeight = 38; table.Columns[1].FillWeight = 20; table.Columns[2].FillWeight = 22; table.Columns[3].FillWeight = 9; table.Columns[4].FillWeight = 14;
        table.Columns[3].MinimumWidth = (int)(78 * DeviceDpi / 96f);
        void FitColumns()
        {
            int logicalWidth = (int)(table.ClientSize.Width * 96f / table.DeviceDpi);
            table.Columns[2].Visible = logicalWidth >= 720;
            table.Columns[1].Visible = logicalWidth >= 520;
            table.Columns[4].Visible = logicalWidth >= 360;
        }
        if (initialize) table.SizeChanged += (_, _) => FitColumns();
        FitColumns();

        for (int i = 0; i < headings.Length; i++) table.Columns[i].HeaderText = headings[i];
        if (initialize) table.CellFormatting += (_, e) => { if (e.RowIndex >= 0 && e.RowIndex < visible.Count && e.ColumnIndex == 4) { var m = visible[e.RowIndex]; e.CellStyle!.ForeColor = m.CueTrackMissing || availability.Get(m.Path, e.RowIndex) == false || m.Cue.Length != 0 && availability.Get(m.Cue, e.RowIndex) == false ? UiTheme.Warning : UiTheme.Muted; } };
    }
}
