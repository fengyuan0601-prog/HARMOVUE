// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

namespace LocalMedia;

public sealed partial class MainWindow
{
    FlowLayoutPanel PageStack()
    {
        var stack = new GlassFlowPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(0, 0, 4, 16) };
        stack.Layout += (_, _) => { int width = Math.Max(220, stack.ClientSize.Width - stack.Padding.Horizontal - (int)(20 * stack.DeviceDpi / 96f)); foreach (Control child in stack.Controls) if (child.Width != width) child.Width = width; };
        content.Controls.Add(stack); stack.BringToFront(); return stack;
    }
    Label SectionHeading(string title, string? hint = null)
    {
        var label = Caption(hint == null ? title : title + "   " + hint, 11, bold: true); label.Height = 38; label.Padding = new Padding(0, 6, 0, 0); return label;
    }
    PictureBox CoverBox(Media media, DockStyle dock = DockStyle.Fill, bool hideWhenMissing = false)
    {
        var picture = new CoverPictureBox { Dock = dock, SizeMode = PictureBoxSizeMode.Zoom, BackColor = UiTheme.Elevated, Visible = !hideWhenMissing };
        picture.Paint += (_, e) => { if (picture.Image == null) UiTheme.Glyph(e.Graphics, media.Kind == "video" ? UiTheme.Video : UiTheme.Music, picture.ClientRectangle, UiTheme.Muted, 30); };
        LoadCover(media, picture, hideWhenMissing ? () => picture.Visible = picture.Image != null : null); return picture;
    }
    SurfacePanel MediaTile(Media media, int width = 208, bool videoTile = false)
    {
        int imageHeight = videoTile ? 117 : 156;
        var panel = new SurfacePanel { Width = width, Height = imageHeight + 76, Margin = new Padding(0, 0, 16, 16), Padding = new Padding(10) };
        var picture = CoverBox(media, DockStyle.Top); picture.Height = imageHeight - 10;
        var title = Caption(media.Title, 10, bold: true); title.Dock = DockStyle.Top; title.Height = 30; title.Padding = new Padding(2, 8, 2, 0);
        string detail = media.Kind == "video" ? media.Watch + " · " + DurationText(media.Duration) : (media.Artist.Length > 0 ? media.Artist : "本地音乐") + " · " + DurationText(media.Duration);
        var subtitle = Caption(detail, 8.5f, UiTheme.Muted); subtitle.Dock = DockStyle.Fill; subtitle.Padding = new Padding(2, 6, 2, 0);
        CheckCardAvailability(subtitle, media);
        panel.Controls.Add(subtitle); panel.Controls.Add(title); panel.Controls.Add(picture);
        var play = IconButton("播放 " + media.Title, UiTheme.Play, () => Play(media, media.Kind == "video" && media.Position > 0), true);
        play.Width = play.Height = 32; play.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        play.Left = width - 50; play.Top = imageHeight - 47; panel.Controls.Add(play); play.BringToFront();
        panel.ContextMenuStrip = Menu(media); picture.ContextMenuStrip = panel.ContextMenuStrip; title.ContextMenuStrip = panel.ContextMenuStrip;
        picture.DoubleClick += (_, _) => Play(media, media.Kind == "video" && media.Position > 0); title.DoubleClick += (_, _) => Play(media, media.Kind == "video" && media.Position > 0);
        if (media.Kind == "video" && media.Duration > 0)
        {
            var track = new Panel { Dock = DockStyle.Bottom, Height = 3, BackColor = UiTheme.Border };
            var fill = new Panel { Dock = DockStyle.Left, BackColor = UiTheme.Accent };
            track.Controls.Add(fill); track.Resize += (_, _) => fill.Width = (int)(track.Width * Math.Clamp(media.Position / media.Duration, 0, 1)); panel.Controls.Add(track);
        }
        return panel;
    }
    void BuildHomeDashboard()
    {
        var stack = PageStack(); var media = store.Media;
        int musicCount = media.Count(m => m.Kind == "music"), videoCount = media.Count(m => m.Kind == "video");
        if (media.Count == 0)
        {
            var empty = new SurfacePanel { Height = 144, Padding = new Padding(26), Margin = new Padding(0, 0, 0, 18) };
            var title = Caption("暂无内容", 15, bold: true); title.Dock = DockStyle.Top; title.Height = 42;
            var actions = new GlassFlowPanel { Dock = DockStyle.Bottom, Height = 44 };
            var add = (UiButton)Button("添加媒体目录", AddRoot, 160, 38); add.Style = UiButtonStyle.Primary; add.Glyph = UiTheme.Add;
            actions.Controls.Add(add); actions.Controls.Add(Button("打开文件", OpenFiles, 114, 38));
            empty.Controls.Add(title); empty.Controls.Add(actions); stack.Controls.Add(empty); return;
        }
        var summary = new SurfacePanel { Height = 64, Padding = new Padding(24, 18, 24, 18), Margin = new Padding(0, 0, 0, 18) };
        var counts = Caption($"{musicCount} 首音乐    {videoCount} 部视频    {media.Count(m => m.Favorite)} 项收藏", 9.5f, UiTheme.Muted); counts.Dock = DockStyle.Fill;
        summary.Controls.Add(counts); stack.Controls.Add(summary);
        Media? music = store.State.Music.Queue.Skip(store.State.Music.Index).Select(id => media.FirstOrDefault(m => m.Id == id)).OfType<Media>().FirstOrDefault() ?? media.Where(m => m.Kind == "music").OrderByDescending(m => m.LastPlayed).FirstOrDefault();
        Media? videoResume = media.Where(m => m.Kind == "video" && m.Watch == "未看完").OrderByDescending(m => m.LastPlayed).FirstOrDefault() ?? media.FirstOrDefault(m => m.Kind == "video");
        var resumes = new TableLayoutPanel { Height = 174, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 0, 16) };
        resumes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); resumes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        resumes.Controls.Add(ResumeCard("继续听", music, false), 0, 0); resumes.Controls.Add(ResumeCard("继续看", videoResume, true), 1, 0); stack.Controls.Add(resumes);
        stack.Controls.Add(SectionHeading("最近添加"));
        var recent = new GlassFlowPanel { Height = 226, WrapContents = false, AutoScroll = true, Margin = new Padding(0, 0, 0, 12) };
        foreach (var item in media.OrderByDescending(m => m.Added).Take(8)) recent.Controls.Add(MediaTile(item, 182));
        stack.Controls.Add(recent);
        stack.Controls.Add(SectionHeading("常用收藏"));
        var favorites = media.Where(m => m.Favorite).OrderByDescending(m => m.LastPlayed).Take(5).ToList();
        if (favorites.Count == 0)
        {
            var label = Caption("暂无收藏", 9.5f, UiTheme.Muted); label.Height = 32; stack.Controls.Add(label);
        }
        else
        {
            var list = new UiListBox { Height = Math.Min(4, favorites.Count) * 54, Margin = new Padding(0, 0, 0, 16) };
            foreach (var item in favorites) list.Items.Add(new MediaRow(item, item.Kind == "music" ? item.Artist : item.Category));
            list.DoubleClick += (_, _) => { if (list.SelectedItem is MediaRow row) Play(row.Media); }; stack.Controls.Add(list);
        }
    }
    SurfacePanel ResumeCard(string heading, Media? media, bool videoCard)
    {
        var card = new SurfacePanel { Dock = DockStyle.Fill, Margin = videoCard ? new Padding(8, 0, 0, 0) : new Padding(0, 0, 8, 0), Padding = new Padding(16) };
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 0, 0, 0) };
        var label = Caption(heading, 9, UiTheme.Accent, true); label.Dock = DockStyle.Top; label.Height = 24;
        var title = Caption(media?.Title ?? (videoCard ? "暂无视频" : "暂无音乐"), 11, bold: true); title.Dock = DockStyle.Top; title.Height = 44;
        if (media != null)
        {
            var detail = Caption(Time(media.Position) + " / " + DurationText(media.Duration), 8.5f, UiTheme.Muted); detail.Dock = DockStyle.Top; detail.Height = 22;
            CheckCardAvailability(detail, media);
            body.Controls.Add(detail);
        }
        var action = (UiButton)Button(media == null ? "添加目录" : "继续播放", () => { if (media == null) AddRoot(); else if (!videoCard && store.State.Music.Queue.Count > 0) ResumeSession("music", true); else Play(media, true); }, 112, 32);
        action.Dock = DockStyle.Bottom; action.Glyph = media == null ? UiTheme.Add : UiTheme.Play; action.Style = UiButtonStyle.Quiet;
        body.Controls.Add(title); body.Controls.Add(label); body.Controls.Add(action); card.Controls.Add(body);
        if (media != null) { var picture = CoverBox(media, DockStyle.Left); picture.Width = 108; card.Controls.Add(picture); }
        return card;
    }
    void CheckCardAvailability(Label detail, Media media)
    {
        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        detail.Disposed += (_, _) => cancellation.Cancel();
        detail.HandleCreated += (_, _) => _ = Task.Run(async () =>
        {
            try { await shelfProbeSlots.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try
            {
                if (token.IsCancellationRequested || media.Online || token.IsCancellationRequested || !detail.IsHandleCreated) return;
                detail.BeginInvoke((Action)(() =>
                {
                    if (detail.IsDisposed) return;
                    detail.Text = "文件暂时离线";
                    detail.ForeColor = UiTheme.Warning;
                }));
            }
            catch (InvalidOperationException) { } // The card may be replaced while probing.
            finally { shelfProbeSlots.Release(); }
        });
    }
    void BuildNowPlaying()
    {
        var media = player.Current; lastNowId = media?.Id;
        if (media == null)
        {
            var empty = new GlassCanvas { Dock = DockStyle.Fill };
            var hint = Caption("尚未播放", 17); hint.Dock = DockStyle.Fill; hint.TextAlign = ContentAlignment.MiddleCenter;
            empty.Controls.Add(hint); content.Controls.Add(empty); empty.BringToFront(); return;
        }
        if (media.Kind == "video")
        {
            var actions = Actions();
            actions.Controls.Add(Button("纯画面播放", EnterVideoPresentation, 130, 36)); actions.Controls.Add(Button("全屏 F11", ToggleVideoFullscreen, 110, 36));
            var more = Button("播放选项", () => { }, 110, 36); more.Click += (_, _) => ShowVideoOptions(more); actions.Controls.Add(more);
            stage.Dock = DockStyle.Fill; content.Controls.Add(stage); stage.BringToFront(); return;
        }
        var host = new GlassCanvas { Dock = DockStyle.Fill };
        var cover = CoverBox(media, DockStyle.None, hideWhenMissing: true);
        var title = Caption(media.Title, 18); title.TextAlign = ContentAlignment.MiddleCenter;
        var artist = Caption(media.Artist.Length > 0 ? media.Artist : "未知歌手", 10, UiTheme.Muted); artist.TextAlign = ContentAlignment.MiddleCenter;
        lyrics = Caption("", 12, UiTheme.Muted); lyrics.TextAlign = ContentAlignment.MiddleCenter;
        var currentLyrics = lyrics;
        UiButton? options = null;
        options = IconButton("更多播放选项", UiTheme.More, () => ShowMusicOptions(options!, media));
        cover.ContextMenuStrip = Menu(media);
        host.Controls.AddRange([cover, title, artist, currentLyrics, options]);
        void FitFocus()
        {
            if (host.IsDisposed || (dpiReady && !dpiScaled.Contains(host))) return;
            float scale = dpiReady ? dpiFactor : 1; int P(float value) => (int)(value * scale);
            int available = Math.Max(0, host.ClientSize.Height - P(32));
            int edge = cover.Visible ? Math.Max(0, Math.Min(P(280), Math.Min(host.ClientSize.Width - P(72), available - P(142)))) : 0;
            int textWidth = Math.Max(0, Math.Min(P(660), host.ClientSize.Width - P(40)));
            int left = (host.ClientSize.Width - textWidth) / 2;
            int top = Math.Max(P(12), (host.ClientSize.Height - edge - P(edge > 0 ? 142 : 118)) / 2);
            cover.SetBounds((host.ClientSize.Width - edge) / 2, top, edge, edge);
            int textTop = top + edge + P(edge > 0 ? 24 : 0);
            title.SetBounds(left, textTop, textWidth, P(36));
            artist.SetBounds(left, textTop + P(39), textWidth, P(24));
            currentLyrics.SetBounds(left, textTop + P(83), textWidth, P(40));
            options.SetBounds(host.ClientSize.Width - P(36), P(4), P(32), P(32));
        }
        host.Resize += (_, _) => FitFocus();
        cover.VisibleChanged += (_, _) => FitFocus();
        if (IsHandleCreated) BeginInvoke((Action)FitFocus);
        content.Controls.Add(host); host.BringToFront(); RefreshQueue(); ReadLyrics(media);
    }

    void ShowMusicOptions(Control anchor, Media media)
    {
        var menu = new ContextMenuStrip { BackColor = UiTheme.Surface, ForeColor = UiTheme.Ink };
        void Item(string label, Action action) => menu.Items.Add(label, null, (_, _) => Try(action));
        Item("迷你播放器", MiniWindow);
        Item(TopMost ? "取消窗口置顶" : "窗口置顶", () => { TopMost = !TopMost; });
        Item("音频详情", () => { using var mediaMenu = Menu(media); mediaMenu.Items.Cast<ToolStripItem>().First(x => x.Text == "音视频详情").PerformClick(); });
        Item("选择音轨…", () => ChooseTrack("audio", "aid"));
        Item("定时停止…", SetStopTimer);
        menu.Items.Add(new ToolStripSeparator());
        Item("查看本地歌词…", () => ShowLocalLyrics(media));
        Item("查看完整播放队列", () => Navigate("播放队列"));
        UiMenus.DisposeAfterClose(menu, this);
        menu.Show(anchor, new Point(0, anchor.Height));
    }

    void ShowLocalLyrics(Media media)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var dialog = new Form { Text = media.Title + " · 本地歌词", Icon = Brand.AppIcon(), AutoScaleMode = AutoScaleMode.None, Size = new Size((int)(560 * dpiFactor), (int)(500 * dpiFactor)), StartPosition = FormStartPosition.CenterParent, BackColor = UiTheme.Background, ForeColor = UiTheme.Ink, Font = Font };
        var text = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = UiTheme.Background, ForeColor = UiTheme.Ink, Font = Font, DetectUrls = false, Text = "", Margin = Padding.Empty };
        dialog.FormClosed += (_, _) => cancel.Cancel();
        dialog.Shown += async (_, _) =>
        {
            try { var result = await LocalLyrics.ReadAsync(media.Path, cancel.Token); if (!text.IsDisposed && !cancel.IsCancellationRequested) text.Text = result.Text; }
            catch (OperationCanceledException) { if (!text.IsDisposed) text.Text = "歌词暂时不可用"; }
            catch (Exception error) { Diagnostics.Log(store.DirectoryPath, error); if (!text.IsDisposed) text.Text = "歌词暂时不可用"; }
        };
        dialog.Padding = new Padding((int)(24 * dpiFactor)); dialog.Controls.Add(text); dialog.ShowDialog(this);
    }
}