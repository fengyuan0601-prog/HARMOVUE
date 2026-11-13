// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

namespace LocalMedia;

public sealed partial class MainWindow
{
    bool immersiveVideo, videoControlsVisible, miniActive, changingVideoPresentation;
    Rectangle presentationBounds, videoFullscreenBounds;
    FormBorderStyle presentationBorder;
    FormWindowState presentationWindowState;
    Panel? videoOverlayTop, videoOverlayBottom;
    Label? videoOverlayTitle;
    DateTime videoInteraction;
    ContextMenuStrip? videoOptions;

    void SyncVideoPresentation()
    {
        if (changingVideoPresentation) return;
        if (player.Current?.Kind != "video") { if (immersiveVideo) ExitVideoPresentation(); return; }
        if (!miniActive && page == "正在播放" && Visible && !immersiveVideo) EnterVideoPresentation();
        if (videoOverlayTitle != null) videoOverlayTitle.Text = player.Current.Title;
    }
    void EnterVideoPresentation()
    {
        if (immersiveVideo || changingVideoPresentation || player.Current?.Kind != "video" || miniActive) return;
        using var measurement = UiPerformance.Measure("video_enter_chrome");
        if (Diagnostics.PlaybackTraceEnabled)
            Diagnostics.TracePlayback("video presentation begin", player.Current.Id, detail: video.IsHandleCreated ? video.Handle.ToInt64().ToString("X") : "none");
        presentationWindowState = WindowState;
        presentationBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        var pictureBounds = WindowState == FormWindowState.Maximized ? Screen.FromControl(this).WorkingArea : Bounds;
        presentationBorder = FormBorderStyle;
        changingVideoPresentation = true;
        SuspendLayout();
        try
        {
            immersiveVideo = true; full = false;
            playerBar.BackColor = UiTheme.Surface;
            UiTheme.WindowChrome(this, false);
            if (stage.Parent != null) stage.Parent.Controls.Remove(stage);
            main.Visible = nav.Visible = nowRail.Visible = false;
            Controls.Add(stage); stage.Dock = DockStyle.Fill; stage.Visible = true; stage.BringToFront();
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = pictureBounds;
            BuildVideoOverlay();
            if (playerBar.Parent != null) playerBar.Parent.Controls.Remove(playerBar);
            videoOverlayBottom!.Controls.Add(playerBar); playerBar.Dock = DockStyle.Fill; playerBar.Visible = true;
            videoControlsVisible = false; videoOverlayTop!.Visible = videoOverlayBottom.Visible = false;
            videoInteraction = DateTime.UtcNow;
        }
        finally
        {
            try { ResumeLayout(true); }
            finally { changingVideoPresentation = false; }
        }
        LayoutChrome();
        LayoutVideoOverlay();
        if (Diagnostics.PlaybackTraceEnabled)
            Diagnostics.TracePlayback("video presentation end", player.Current.Id, detail: video.IsHandleCreated ? video.Handle.ToInt64().ToString("X") : "none");
    }
    void ExitVideoPresentation()
    {
        if (!immersiveVideo || changingVideoPresentation) return;
        using var measurement = UiPerformance.Measure("video_exit_chrome");
        changingVideoPresentation = true;
        SuspendLayout();
        try
        {
            immersiveVideo = false; full = false;
            videoOptions?.Close();
            if (videoOverlayTop != null) videoOverlayTop.Visible = false;
            if (videoOverlayBottom != null) videoOverlayBottom.Visible = false;
            // Reparent the bar while hidden and use its final browse height.
            // Showing 116px first used to provoke a second resize down to 88px.
            playerBar.Visible = false;
            playerBar.BackColor = UiTheme.Sidebar;
            playerBar.Floating = true;
            if (playerBar.Parent != null) playerBar.Parent.Controls.Remove(playerBar);
            playerBar.Dock = DockStyle.Bottom;
            playerBar.Height = (int)(88 * dpiFactor);
            Controls.Add(playerBar);
            if (stage.Parent != null) stage.Parent.Controls.Remove(stage);
            // Keep the browse HWNDs hidden until the frame and final geometry
            // are restored. The playback HWND is retained, never redrawn here.
            FormBorderStyle = presentationBorder;
            WindowState = FormWindowState.Normal;
            Bounds = presentationBounds;
            WindowState = presentationWindowState;
            UiTheme.WindowChrome(this);
        }
        finally
        {
            try
            {
                main.Visible = nav.Visible = playerBar.Visible = true;
                nowRail.Visible = queueDrawerOpen;
            }
            finally
            {
                try { ResumeLayout(true); }
                finally { changingVideoPresentation = false; }
            }
        }
        LayoutChrome();
        ApplyVideoSubtitlePosition();
        // Request the restored chrome together, without forcing synchronous
        // painting or changing Form/video redraw. Render batches content itself.
        toolbar.Invalidate(true);
        nav.Invalidate(true);
        playerBar.Invalidate(true);
        if (nowRail.Visible) nowRail.Invalidate(true);
    }
    void BuildVideoOverlay()
    {
        if (videoOverlayTop != null) return;
        int P(float value) => (int)(value * dpiFactor);
        videoOverlayTop = new Panel { BackColor = UiTheme.Surface };
        videoOverlayBottom = new Panel { BackColor = UiTheme.Surface };
        var back = (UiButton)Button("返回媒体库", () => Navigate("视频库"), 136, 38); back.Glyph = "\uE72B"; back.Style = UiButtonStyle.Quiet;
        var subtitles = (UiButton)Button("字幕", () => ChooseTrack("sub", "sid"), 68, 38); subtitles.Style = UiButtonStyle.Quiet;
        var audio = (UiButton)Button("音轨", () => ChooseTrack("audio", "aid"), 68, 38); audio.Style = UiButtonStyle.Quiet;
        var more = (UiButton)Button("更多", () => { }, 68, 38); more.Style = UiButtonStyle.Quiet;
        more.Click += (_, _) => ShowVideoOptions(more);
        var fullscreen = IconButton("全屏 / 退出全屏 · F11", UiTheme.Fullscreen, ToggleVideoFullscreen);
        var close = IconButton("关闭播放器", "\uE8BB", Close);
        videoOverlayTitle = Caption(player.Current?.Title ?? "视频", 11, bold: true);
        videoOverlayTop.Controls.AddRange([back, videoOverlayTitle, subtitles, audio, more, fullscreen, close]);
        void Arrange()
        {
            int width = videoOverlayTop.Width;
            back.SetBounds(P(16), P(12), P(136), P(38));
            close.SetBounds(width - P(54), P(12), P(38), P(38)); fullscreen.SetBounds(width - P(100), P(12), P(38), P(38));
            more.SetBounds(width - P(176), P(12), P(68), P(38)); audio.SetBounds(width - P(248), P(12), P(68), P(38)); subtitles.SetBounds(width - P(320), P(12), P(68), P(38));
            videoOverlayTitle.SetBounds(P(168), P(12), Math.Max(P(30), width - P(500)), P(38));
        }
        videoOverlayTop.Resize += (_, _) => Arrange(); Arrange();
        stage.Controls.Add(videoOverlayTop); stage.Controls.Add(videoOverlayBottom);
        stage.Resize += (_, _) => LayoutVideoOverlay();
        void Observe(Control control)
        {
            control.MouseMove += (_, _) => { if (immersiveVideo && videoControlsVisible) videoInteraction = DateTime.UtcNow; };
            control.MouseDown += (_, _) => videoInteraction = DateTime.UtcNow;
            foreach (Control child in control.Controls) Observe(child);
        }
        Observe(videoOverlayTop); Observe(playerBar);
    }
    void LayoutVideoOverlay()
    {
        if (changingVideoPresentation || videoOverlayTop == null || videoOverlayBottom == null) return;
        videoOverlayTop.SetBounds(0, 0, stage.Width, (int)(64 * dpiFactor));
        videoOverlayBottom.SetBounds(0, Math.Max(0, stage.Height - (int)(116 * dpiFactor)), stage.Width, (int)(116 * dpiFactor));
        videoOverlayTop.BringToFront(); videoOverlayBottom.BringToFront(); LayoutPlayerBar();
        if (videoControlsVisible) ApplyVideoSubtitlePosition();
    }
    void ToggleVideoControls()
    {
        if (!immersiveVideo) return;
        SetVideoControls(!videoControlsVisible);
    }
    void SetVideoControls(bool visible)
    {
        if (!immersiveVideo || videoOverlayTop == null || videoOverlayBottom == null) return;
        videoControlsVisible = visible;
        videoOverlayTop.Visible = videoOverlayBottom.Visible = visible;
        videoInteraction = DateTime.UtcNow;
        if (visible) { LayoutVideoOverlay(); UpdatePlayerChrome(); }
        else ApplyVideoSubtitlePosition();
    }
    void ApplyVideoSubtitlePosition()
    {
        double position = store.State.SubtitlePosition;
        if (immersiveVideo && videoControlsVisible && videoOverlayBottom != null && stage.Height > 0)
        {
            // mpv's position is a percentage of the video viewport. Raise only
            // as far as necessary to clear our controls, keeping a deliberately
            // higher user position and restoring the exact setting on hide.
            double safe = 100 - 100d * (videoOverlayBottom.Height + 12 * dpiFactor) / stage.Height;
            position = Math.Min(position, Math.Clamp((int)Math.Floor(safe), 0, 100));
        }
        player.SetEngineOption("sub-pos", position);
    }
    void AutoHideVideoControls()
    {
        if (immersiveVideo && videoControlsVisible && Form.ActiveForm == this && videoOptions?.Visible != true && openPlaybackMenus == 0 && !dragging && !player.Paused && DateTime.UtcNow - videoInteraction > TimeSpan.FromSeconds(3)) SetVideoControls(false);
    }
    void ToggleVideoFullscreen()
    {
        if (!immersiveVideo) EnterVideoPresentation();
        if (!immersiveVideo) return;
        if (!full) { videoFullscreenBounds = Bounds; WindowState = FormWindowState.Normal; Bounds = Screen.FromControl(this).Bounds; full = true; }
        else { WindowState = FormWindowState.Normal; Bounds = videoFullscreenBounds; full = false; }
        SetVideoControls(false);
    }
    void ShowVideoOptions(Control anchor)
    {
        if (player.Current is not Media media) return;
        videoOptions?.Dispose(); videoOptions = new ContextMenuStrip { BackColor = UiTheme.Surface, ForeColor = UiTheme.Ink, ShowImageMargin = false };
        var menu = videoOptions;
        void Add(string title, Action action, bool enabled = true) { var item = menu.Items.Add(title, null, (_, _) => Try(action)); item.Enabled = enabled; }
        Add("播放速度…", () => { var value = Choose("播放速度", new[] { ".5", ".75", "1", "1.25", "1.5", "2" }.Select(v => new Choice(v, v + " 倍速")), player.Session.Speed.ToString(System.Globalization.CultureInfo.InvariantCulture)); if (double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double speed)) { player.Session.Speed = speed; player.Engine.Set("speed", speed); } });
        Add("加载外挂字幕…", () => { using var file = new OpenFileDialog { Filter = "字幕|*.srt;*.ass;*.ssa;*.vtt;*.sub;*.idx;*.sup;*.mks|所有文件|*.*" }; if (file.ShowDialog(this) == DialogResult.OK) player.QueueSubtitle(file.FileName); });
        Add("关闭字幕", () => { player.SelectTrack("sid", "no"); media.Subtitle = "no"; });
        Add("字幕编码…", ChooseSubtitleEncoding);
        Add("字幕外观…", SubtitleAppearance, !BitmapSubtitleSelected());
        var sync = new ToolStripMenuItem("同步调整");
        void Sync(string label, Action action) => sync.DropDownItems.Add(label, null, (_, _) => Try(action));
        Sync("字幕提前 0.1 秒", () => { media.SubDelay -= .1; player.Engine.Set("sub-delay", media.SubDelay); });
        Sync("字幕延后 0.1 秒", () => { media.SubDelay += .1; player.Engine.Set("sub-delay", media.SubDelay); });
        Sync("声音提前 0.1 秒", () => { media.AudioDelay -= .1; player.Engine.Set("audio-delay", media.AudioDelay); });
        Sync("声音延后 0.1 秒", () => { media.AudioDelay += .1; player.Engine.Set("audio-delay", media.AudioDelay); });
        menu.Items.Add(sync); menu.Items.Add(new ToolStripSeparator());
        bool LoopPoint(string property, out double value) => double.TryParse(player.Engine.Get(property), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) && double.IsFinite(value) && value >= 0;
        bool hasA = LoopPoint("ab-loop-a", out double loopA), hasB = LoopPoint("ab-loop-b", out double loopB);
        string loopTitle = hasA ? hasB ? $"A-B 循环 · 关闭（{Time(loopA)}–{Time(loopB)}）" : $"A-B 循环 · 设置终点（A {Time(loopA)}）" : "A-B 循环 · 设置起点";
        Add("章节…", Chapters); Add(loopTitle, player.CycleAbLoop);
        Add("添加当前时间书签", () => { media.Bookmarks.Add(media.Position); store.SaveMetadata(media); });
        Add("跳转书签…", () => { var value = Choose("时间书签", media.Bookmarks.Select((time, index) => new Choice(index.ToString(), Time(time))), ""); if (int.TryParse(value, out int index) && index >= 0 && index < media.Bookmarks.Count) SeekPlayback(media.Bookmarks[index]); });
        Add("画面比例…", () => { var ratio = Prompt("输入比例：-1 自动、16:9、4:3", "-1"); if (ratio != null) player.Engine.Set("video-aspect-override", ratio); });
        Add("定时停止…", SetStopTimer); Add("保存截图…", Screenshot); Add("媒体详情…", () => Menu(media).Items.Cast<ToolStripItem>().First(x => x.Text == "音视频详情").PerformClick());
        menu.Items.Add(new ToolStripSeparator());
        Add("迷你窗口", () => { ExitVideoPresentation(); MiniWindow(); }); Add("返回视频库", () => Navigate("视频库"));
        menu.Closed += (_, _) => videoInteraction = DateTime.UtcNow;
        menu.Show(anchor, new Point(0, anchor.Height));
    }
    void SetStopTimer()
    {
        var text = Prompt("多少分钟后暂停？0 表示本曲/本集结束后停止", "30");
        if (double.TryParse(text, out double minutes) && minutes >= 0) { player.StopAtEnd = minutes == 0; player.StopAt = minutes > 0 ? DateTime.Now.AddMinutes(minutes) : null; }
    }
}
