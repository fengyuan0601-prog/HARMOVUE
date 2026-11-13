// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalMedia;

public sealed partial class MainWindow : Form
{
    readonly UiPerformance uiPerformance;
    readonly D3D11Warmup gpuWarmup = new();
    bool mainFirstPaintObserved;
    readonly UiRedrawBatch browseRedraw;
    DataGridView? libraryGrid;
    GlassCanvas? libraryTableHost;
    GridFileAvailability? libraryAvailability;
    static readonly SemaphoreSlim coverReadGate = new(2, 2);
    readonly CancellationTokenSource coverLifetime = new();
    readonly Dictionary<PictureBox, CancellationTokenSource> coverRequests = [];
    sealed record CoverSource(string Id, string Path, string Folder, string Kind, string CacheDirectory, string CoverPath);
    readonly HashSet<Font> ownedFonts = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<(string Family, float Size, FontStyle Style), Font> sharedFonts = [];
    Font OwnedFont(float size, FontStyle style = FontStyle.Regular)
    {
        var key = (Font.FontFamily.Name, size, style);
        if (sharedFonts.TryGetValue(key, out var existing)) return existing;
        var font = new Font(Font.FontFamily, size, style);
        sharedFonts.Add(key, font); ownedFonts.Add(font); return font;
    }
    readonly Store store; readonly Playback player = null!; SystemIntegration? integration; CancellationTokenSource thumbs = new(); readonly HashSet<Control> dpiScaled = []; bool dpiReady; float dpiFactor = 1; readonly Panel content = new GlassCanvas() { Dock = DockStyle.Fill, Padding = new Padding(32, 0, 32, 24) };
    readonly Panel video = new() { BackColor = Color.Black, Dock = DockStyle.Fill }; readonly Panel stage = new() { Dock = DockStyle.Top, Height = 280 };
    string lastError="";DateTime messageUntil;int scanTicks;
    // A large scan must not enqueue one UI callback per media file.
    sealed class ScanStatusProgress : IProgress<string>
    {
        string? latest;
        public void Report(string value) => Volatile.Write(ref latest, value);
        public string? Take() => Interlocked.Exchange(ref latest, null);
    }
    readonly ScanStatusProgress scanProgress = new();
    CancellationTokenSource? openingFiles;
    int scanGeneration;
    bool restoringLibrary;
    bool finalClosePosted, finalCloseReady;
    Task? scanCompletion, restoreWork;
    readonly Label status = new GlassLabel() { Dock = DockStyle.Bottom, Height = 26, ForeColor = Color.Silver };
    readonly Label now = new GlassLabel() { AutoSize = false, Width = 280, Height = 54, Text = "尚未播放" };
    readonly Label clock = new GlassLabel() { Width = 140, Text = "00:00 / 00:00", TextAlign = ContentAlignment.MiddleCenter };
    readonly TimelineSlider seek = new() { Minimum = 0, Maximum = 10000, Width = 260 };
    readonly TimelineSlider volume = new() { Minimum = 0, Maximum = 100, Width = 110 };
    Button? subtitleAppearanceButton; bool compactChrome;
    readonly TextBox search = new GlassTextBox() { Width = 300, PlaceholderText = "搜索" };
    readonly System.Windows.Forms.Timer searchTimer = new() { Interval = 180 };
    readonly MediaSearch mediaSearch = new();
    SearchDocument[] searchDocuments = [];
    HashSet<string> searchMatches = [];
    string indexedQuery = "";
    bool searchPending;
    int searchRevision, searchStoreRevision = -1, searchDocumentsRevision = -1;
    (string Page, string Group, MediaList? List)? searchReturn;
    CancellationTokenSource searchCancel = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 200 }; readonly Panel nav = new GlassCanvas() { Dock = DockStyle.Left, Width = 76 };
    readonly Panel main = new GlassCanvas() { Dock = DockStyle.Fill }; readonly Panel toolbar = new GlassCanvas() { Dock = DockStyle.Top, Height = 96 };
    readonly ListBox queue = new UiListBox { Dock = DockStyle.Fill }; string page = "首页"; List<Media> visible = []; DataGridView? grid; Panel? cards; bool dragging; bool full; FormBorderStyle priorBorder; Rectangle priorBounds; CancellationTokenSource? scan; Task? scanWork; bool shuttingDown; string group = "全部"; MediaList? selectedList;
    public MainWindow(string data, string[] args) : this(data, args, null) { }
    internal MainWindow(string data, string[] args, Store? preparedStore, bool deferPlayback = false, NativeWarmup? nativeWarmup = null, bool? startupVideoIntent = null)
    {
        uiPerformance = new UiPerformance(data);
        browseRedraw = new UiRedrawBatch(content);
        Disposed += (_, _) => gpuWarmup.Dispose();
        bool resourcesDisposed = false;
        Disposed += (_, _) => { if (resourcesDisposed) return; resourcesDisposed = true; queueMotion?.Dispose(); headingMotion?.Dispose(); coverLifetime.Cancel(); coverLifetime.Dispose(); browseRedraw.Dispose(); uiPerformance.Dispose(); libraryTableHost?.Dispose(); libraryGrid?.Dispose(); DisposeLibraryNavigationCache(); };
        using var startupMeasurement = UiPerformance.Measure("window_constructor");
        Diagnostics.TraceUi(data, "constructor");
        AutoScaleMode = AutoScaleMode.None;
        Text = "HARMOVUE"; Icon = Brand.AppIcon(); ShowIcon = false;
        Size = new Size(1280, 860);
        MinimumSize = new Size(900, 620);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = UiTheme.Background;
        ForeColor = UiTheme.Ink;
        Font = new Font("Microsoft YaHei UI", 10.5f);
        var applicationFont = Font;
        Disposed += (_, _) => { foreach (var font in ownedFonts) font.Dispose(); ownedFonts.Clear(); applicationFont.Dispose(); };
        KeyPreview = true;
        AllowDrop = true;
        using (UiPerformance.Measure("window_store_construct_call")) store = preparedStore ?? new Store(data);
        Diagnostics.TraceUi(data, "store ready");
        bool previousLight = UiTheme.IsLight;
        UiTheme.SetLight(store.State.Theme == "light");
        page = store.State.Page == "首页" ? "音乐库" : store.State.Page;
        BuildChrome();
        UiTheme.RefreshControlColors(this, previousLight);
        // This field is attached later to the queue page, so it is not yet
        // included in the persistent chrome's palette walk.
        if (!Contains(queue)) UiTheme.RefreshControlColors(queue, previousLight);
        Diagnostics.TraceUi(data, "chrome ready");
        using (UiPerformance.Measure("window_video_attach")) stage.Controls.Add(video);
        nint handle;
        using (UiPerformance.Measure("window_video_handle")) handle = video.Handle;
        using (UiPerformance.Measure("window_playback_construct_call")) player = new Playback(store, handle, silent: false, pcm: null, deferEngine: deferPlayback, nativeWarmup: nativeWarmup);
        Diagnostics.TraceUi(data, deferPlayback ? "playback shell ready" : "playback ready");
        queue.DoubleClick += QueuePlay;
        video.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left && e.Clicks == 1) ToggleVideoControls(); };
        video.DoubleClick += (_, _) => { if (immersiveVideo) ToggleVideoFullscreen(); };
        video.MouseMove += (_, _) => { if (immersiveVideo && videoControlsVisible) videoInteraction = DateTime.UtcNow; };
        Shown += (_, _) => { if (WindowState == FormWindowState.Normal) { var area = Screen.FromControl(this).WorkingArea; Location = new Point(area.Left + Math.Max(0, (area.Width - Width) / 2), area.Top + Math.Max(0, (area.Height - Height) / 2)); } SyncVideoPresentation(); };
        player.Changed += () => {
            now.Text = player.Current == null ? "尚未播放" : player.Current.Title + "\n" + player.Current.Artist;
            if (player.Current == null) { clock.Text = "00:00 / 00:00"; seek.Value = 0; }
            UpdatePlayerChrome(); RefreshQueue(); if (page == "正在播放" && lastNowId != player.Current?.Id) Render();
            SyncVideoPresentation();
        };
        player.Failed += s => { status.Text = s; messageUntil = DateTime.Now.AddSeconds(8); if (immersiveVideo) { SetVideoControls(true); if (videoOverlayTitle != null) videoOverlayTitle.Text = "无法播放 · " + s; } };
        void ObserveMainFirstPaint(object? sender, PaintEventArgs e)
        {
            Paint -= ObserveMainFirstPaint;
            mainFirstPaintObserved = true;
        }
        void StartBackgroundServices(object? sender, PaintEventArgs e)
        {
            Paint -= StartBackgroundServices;
            BeginInvoke((Action)(async () =>
            {
                if (shuttingDown || IsDisposed) return;
                // The shared entry prepares video as well as audio. Explicit audio
                // arguments skip GPU work; the ordinary library entry warms once
                // while the independent native helper is still loading the image.
                if (startupVideoIntent != false) RequestGpuWarmup();
                if (deferPlayback)
                {
                    try
                    {
                        Diagnostics.TraceUi(data, "mpv warmup begin");
                        await player.PrepareEngineAsync();
                        if (shuttingDown || IsDisposed) return;
                        Diagnostics.TraceUi(data, "mpv warmup end");
                    }
                    catch (Exception error)
                    {
                        if (shuttingDown || IsDisposed) return;
                        Try(() => Diagnostics.Log(data, error));
                        status.Text = "播放内核暂未就绪。可继续浏览，播放时会重试。";
                    }
                }
                // A play request can be waiting on the same engine task.
                // Let it submit before optional WinRT/device discovery work.
                await Task.Yield();
                if (shuttingDown || IsDisposed) return;
                Diagnostics.TraceUi(data, "system integration begin");
                Try(() => integration = new SystemIntegration(this, player, store.DirectoryPath));
                Diagnostics.TraceUi(data, "system integration end");
            }));
        }
        Paint += ObserveMainFirstPaint;
        if (deferPlayback) Paint += StartBackgroundServices;
        else Shown += (_, _) => Try(() => integration = new SystemIntegration(this, player, store.DirectoryPath));
        search.TextChanged += (_, _) => { searchTimer.Stop(); searchTimer.Start(); };
        FormClosed += (_, _) => { searchCancel.Cancel(); searchCancel.Dispose(); };
        FormClosed += (_, _) => { openingFiles?.Cancel(); openingFiles?.Dispose(); openingFiles = null; };
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); RenderSearchEdit(); };
        seek.MouseDown += (_, _) => dragging = true;
        seek.ValueCommitted += (_, _) => { dragging = false; SeekPlayback(seek.Value / 10000.0 * player.CurrentDuration); };
        DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        DragDrop += async (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) await OpenPaths(paths); };
        FormClosing += async (_, e) =>
        {
            if (finalCloseReady) return;
            if (finalClosePosted) { e.Cancel = true; return; }
            if (!shuttingDown && !exitRequested && e.CloseReason == CloseReason.UserClosing && store.State.CloseToTray)
            { e.Cancel = true; Hide(); tray.Visible = true; return; }
            gpuWarmup.Dispose();
            nativeWarmup?.Dispose();
            shuttingDown = true; CancelPendingPlayback(); openingFiles?.Cancel(); watcher.Dispose(); tray.Dispose(); CancelScans();
            if (scan != null || restoreWork != null)
            {
                e.Cancel = true;
                try { if (scanCompletion != null) await scanCompletion; if (restoreWork != null) await restoreWork; } catch { }
                if (!IsDisposed) BeginInvoke(Close);
                return;
            }
            timer.Stop(); searchTimer.Stop(); searchTimer.Dispose(); thumbs.Cancel(); thumbs.Dispose();
            integration?.Dispose(); player.Dispose(); stage.Dispose(); queue.Dispose(); store.Dispose();
            // UIA disconnection makes outbound COM calls. Post this final step
            // outside the synchronous WM_CLOSE/SendMessage stack, after data
            // and playback have settled and before destroying native controls.
            e.Cancel = true; finalClosePosted = true;
            BeginInvoke((Action)(async () =>
            {
                await UiAccessibility.PrepareExitAsync();
                // Providers are now disconnected process-wide. Drop the
                // virtual rows before HWND destruction, which otherwise asks
                // UIA to disconnect every materialized cell again.
                if (libraryGrid is { IsDisposed: false })
                    using (UiPerformance.Measure("shutdown_grid_clear")) libraryGrid.RowCount = 0;
                uiPerformance.Dispose();
                finalCloseReady = true; Close();
            }));
        };
        timer.Tick += (_, _) => Try(Tick);
        InitializeLibraryTools(); timer.Start();
        Render();
        Diagnostics.TraceUi(data, "initial render ready");
        dpiFactor = DeviceDpi / 96f;
        foreach (Control child in Controls)
        {
            child.Scale(new SizeF(dpiFactor, dpiFactor));
            MarkScaled(child);
        }
        var workArea = Screen.FromControl(this).WorkingArea;
        Size = new Size(Math.Min((int)(1280 * dpiFactor), workArea.Width - (int)(20 * dpiFactor)), Math.Min((int)(840 * dpiFactor), workArea.Height - (int)(12 * dpiFactor)));
        MinimumSize = new Size((int)(900 * dpiFactor), (int)(620 * dpiFactor));
        // Acceptance-only initial geometry; requires an explicitly isolated data profile.
        string? testSize = args.FirstOrDefault(a => a.StartsWith("--test-window-size=", StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LOCALMEDIA_DATA")) && testSize != null)
        {
            var dimensions = testSize[19..].Split('x');
            if (dimensions.Length == 2 && int.TryParse(dimensions[0], out int width) && int.TryParse(dimensions[1], out int height) && width >= 900 && width <= 1600 && height >= 620 && height <= 1200)
                Size = new Size((int)(width * dpiFactor), (int)(height * dpiFactor));
        }
        dpiReady = true;
        SizeChanged += (_, _) => ResponsiveChrome();
        ResponsiveChrome();
        layoutAlbumShelf?.Invoke();
        Diagnostics.TraceUi(data, "DPI ready");
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LOCALMEDIA_DATA")) && args.Contains("--test-audit-window"))
            Text = "HARMOVUE · " + Application.ProductVersion.Split('+')[0] + " 流畅度审计";
        if (Diagnostics.UiTraceEnabled || args.Contains("--test-audit-window"))
            Shown += (_, _) => File.WriteAllText(Path.Combine(store.DirectoryPath, "layout.json"), JsonSerializer.Serialize(new { DeviceDpi, Width, Height, FontSize = Font.Size, Nav = nav.Width, NavFont = nav.Font.Size, Button = navigationButtons[0].Width, ButtonFont = navigationButtons[0].Font.Size, Screen = Screen.FromControl(this).WorkingArea.ToString(), AutoScaleDimensions, CurrentAutoScaleDimensions }));
        if (args.Length > 0)
            Shown += async (_, _) => await OpenPaths(args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray());
    }
    void ResponsiveChrome()
    {
        StopQueueMotion();
        bool compact = ClientSize.Width < 1180 * dpiFactor;
        bool modeChanged = compact != compactChrome; compactChrome = compact;
        LayoutChrome(); LayoutVideoOverlay();
        if (modeChanged && dpiReady && !full && !immersiveVideo && !changingVideoPresentation && page == "正在播放") Render();
    }
    void MarkScaled(Control c)
    {
        dpiScaled.Add(c);
        foreach (Control child in c.Controls)
            MarkScaled(child);
    }
    void Release(Control c)
    {
        // Keep the one virtual table/its HWND between browse pages. Destroying
        // it per navigation also disconnects every materialized UIA cell.
        if (ReferenceEquals(c, libraryTableHost)) { if (c.Parent != content) { c.Parent?.Controls.Remove(c); content.Controls.Add(c); } return; }
        if (RetainLibraryShelf(c) || RetainLibraryChrome(c)) return;
        dpiScaled.Remove(c);
        foreach (Control child in c.Controls.Cast<Control>().ToArray())
            Release(child);
        if (c is PictureBox p)
            p.Image?.Dispose();
        if (c.ContextMenuStrip is { } menu)
        {
            c.ContextMenuStrip = null;
            UiMenus.DisposeDeferred(menu, this);
        }
        c.Dispose();
        // Shared immutable fonts live until the window closes. DPI scaling can
        // replace a control's font; per-page ownership lost those originals.
    }
    static Color C(int r, int g, int b) => Color.FromArgb(r, g, b);
    Button Button(string text, Action action, int width = 115, int height = 34)
    {
        var b = new UiButton { Text = text, AccessibleName = text, Width = width, Height = height, BackColor = UiTheme.Elevated, ForeColor = ForeColor, Margin = new Padding(0, 0, 8, 8), Cursor = Cursors.Hand };
        b.FlatAppearance.BorderSize = 0;
        b.Click += (_, _) => Try(action);
        return b;
    }
    void Try(Action a)
    {
        try
        {
            a();
        }
        catch (Exception e) { status.Text = e.Message;messageUntil=DateTime.Now.AddSeconds(8);if(lastError!=e.ToString()){lastError=e.ToString();Diagnostics.Log(store.DirectoryPath,e);} }
    }
    void Title(string title, string sub)
    {
        string previousTitle = pageHeading.Text;
        pageHeading.Text = title switch { "音乐库" => "音乐", "视频库" => "视频", "正在播放" => "正在聆听", _ => title };
        bool libraryTitle = title is "音乐库" or "视频库";
        pageDescription.Visible = libraryTitle;
        if (libraryTitle)
        {
            bool music = title == "音乐库";
            int count = LibrarySnapshot().Count(media => media.Kind == (music ? "music" : "video"));
            pageDescription.Visible = count > 0;
            pageDescription.Text = count > 0 ? count.ToString("N0") + (music ? " 首歌曲" : " 部视频") : "";
        }
        else pageDescription.Text = sub;
        LayoutHeader();
        if (pageHeading.Text != previousTitle && dpiReady) AnimateHeading();
    }
    FlowLayoutPanel Actions()
    {
        var actions = new GlassFlowPanel { Dock = DockStyle.Top, Height = 54, MinimumSize = new Size(0, 54), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, AutoScroll = false, Margin = Padding.Empty };
        content.Controls.Add(actions); actions.BringToFront(); return actions;
    }
    void Render()
    {
        StopQueueMotion();
        using var measurement = UiPerformance.Measure("render_sync", page);
        long renderStarted = UiPerformance.Timestamp;
        if (immersiveVideo && page != "正在播放") ExitVideoPresentation();
        if (immersiveVideo && player.Current?.Kind == "video" && page == "正在播放") { if (videoOverlayTitle != null) videoOverlayTitle.Text = player.Current.Title; return; }
        int redrawToken = browseRedraw.Begin();
        try
        {
        foreach (var item in navigationButtons)
        {
            string route = item.AccessibleName ?? item.Text;
            item.Selected = route == page || page is "正在播放" or "播放队列" && route == (player.Current?.Kind == "video" ? "视频库" : "音乐库");
        }
        LayoutChrome();
        thumbs.Cancel();
        thumbs.Dispose();
        thumbs = new();
        store.State.Page = page;
        content.SuspendLayout();
        try
        {
        using (UiPerformance.Measure("render_release", page))
        {
        InvalidateLibraryChromeEnvironment();
        if (stage.Parent != null)
            stage.Parent.Controls.Remove(stage);
        if (queue.Parent != null)
            queue.Parent.Controls.Remove(queue);
        foreach (Control c in content.Controls.Cast<Control>().ToArray())
            Release(c);
        if (libraryTableHost is { IsDisposed: false } retainedTable && !WantsLibraryTable()) retainedTable.Visible = false;
        HideLibraryShelves();
        HideLibraryChrome();
        grid = null;
        cards = null;
        layoutAlbumShelf = null;
        musicQueue = null;
        subtitleAppearanceButton = null;
        }
        using (UiPerformance.Measure("render_build", page))
        {
        Title(page == "首页" && search.Text.Trim().Length > 0 ? "搜索结果" : page, page == "首页" && search.Text.Trim().Length > 0 ? "跨音乐与视频 · " + search.Text.Trim() : page switch
        {
            "首页" => "你的音乐与影像，在同一个地方。",
            "音乐库" => "歌曲 · 专辑 · 歌手 · 文件夹",
            "视频库" => "本地影像 · 观看进度 · 分类与集合",
            "设置" => "数据存储在 E: · 原媒体只读 · 无联网内容服务",
            _ => "浏览页面不会中断播放"
        });
        if (page == "设置")
            Settings();
        else if (page == "正在播放")
            NowPlaying();
        else if (page == "播放队列")
        {
            BuildQueuePage();
        }
        else if (page == "歌单与集合")
            Lists();
        else
            Library();
        }
        using (UiPerformance.Measure("render_scale", page))
        {
        if (dpiReady)
            foreach (Control child in content.Controls)
                if (!dpiScaled.Contains(child) && child != stage)
                {
                    child.Scale(new SizeF(dpiFactor, dpiFactor));
                    MarkScaled(child);
                }
        }
        }
        finally { content.ResumeLayout(); }
        UpdateNowRail(); LayoutChrome(); layoutAlbumShelf?.Invoke();
        SyncVideoPresentation();
        }
        finally
        {
            browseRedraw.CompleteAfterLayout(redrawToken);
            uiPerformance.RenderDispatch(this, renderStarted, page);
        }
    }
    bool WantsLibraryTable() => page switch
    {
        "音乐库" => group != "全部" || libraryMode is not ("专辑" or "歌手"),
        "视频库" => !useCards,
        "收藏" => true,
        "首页" => search.Text.Trim().Length > 0 || group != "全部",
        _ => false
    };
    IEnumerable<Media> Filter()
    {
        if (page is "音乐库" or "视频库" && group == "全部" && selectedList == null && search.Text.Trim().Length == 0)
            return DefaultLibraryView(page == "音乐库" ? "music" : "video");
        var library = LibrarySnapshot();
        IEnumerable<Media> q = library;
        if (page == "音乐库")
            q = q.Where(m => m.Kind == "music");
        if (page == "视频库")
            q = q.Where(m => m.Kind == "video");
        if (page == "收藏")
            q = q.Where(m => m.Favorite);
        if (selectedList != null)
        {
            var indexed = library.ToDictionary(m => m.Id);
            q = selectedList.Items.Select(id => indexed.GetValueOrDefault(id)).OfType<Media>();
        }
        if (group == "继续观看")
            q = q.Where(m => m.Kind == "video" && m.Watch == "未看完");
        else if (group == "最近播放")
            q = q.Where(m => m.LastPlayed != null).OrderByDescending(m => m.LastPlayed);
        else if (group == "最近添加")
            q = q.OrderByDescending(m => m.Added);
        else if (group is "未看" or "未看完" or "已看")
            q = q.Where(m => m.Watch == group);
        else if (group.StartsWith("分类："))
            q = q.Where(m => m.Category == group[3..]);
        else if (group.StartsWith("目录：")) q = q.Where(m => m.Folder.Equals(group[3..], StringComparison.OrdinalIgnoreCase) || m.Folder.StartsWith(group[3..].TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        else if (group.StartsWith("文件夹："))
            q = q.Where(m => m.Folder == group[4..]);
        else if (group.StartsWith("专辑ID：")) q = q.Where(m => AlbumKey(m) == group[5..]);
        else if (group.StartsWith("专辑："))
            q = q.Where(m => m.Album == group[3..]);
        else if (group.StartsWith("歌手："))
            q = q.Where(m => m.Artist == group[3..]);
        string term = search.Text.Trim();
        if (term.Length > 0)
        {
            EnsureSearch(term);
            q = q.Where(m => searchMatches.Contains(m.Id));
        }
        else if (indexedQuery.Length > 0)
        {
            searchCancel.Cancel(); searchRevision++; indexedQuery = "";
            // Keep the immutable, revision-keyed snapshot for the next query.
            searchMatches = []; searchPending = false;
        }
        return selectedList != null || group is "最近播放" or "最近添加" ? q : q.OrderBy(m => m.NaturalAlbum).ThenBy(m => m.Disc).ThenBy(m => m.Track).ThenBy(m => m.NaturalTitle);
    }
    void EnsureSearch(string term)
    {
        if (shuttingDown) return;
        int libraryRevision = store.Revision;
        if (indexedQuery == term && searchStoreRevision == libraryRevision) return;
        searchCancel.Cancel(); searchCancel.Dispose(); searchCancel = new();
        indexedQuery = term; searchStoreRevision = libraryRevision; searchMatches = []; searchPending = true;
        var cached = searchDocumentsRevision == libraryRevision ? searchDocuments : null;
        _ = CompleteSearch(cached, term, ++searchRevision, libraryRevision, searchCancel.Token);
    }
    void RenderSearchEdit()
    {
        if (search.Text.Trim().Length > 0)
        {
            if (page != "首页" || searchReturn == null) searchReturn = (page, group, selectedList);
            page = "首页"; group = "全部"; selectedList = null;
        }
        else
        {
            // Closing search returns to the library the user was browsing.
            // Avoid constructing and then immediately disposing the dashboard.
            if (page == "首页" && searchReturn is { } previous) (page, group, selectedList) = previous;
            searchReturn = null;
        }
        Render();
    }
    async Task CompleteSearch(SearchDocument[]? cached, string term, int revision, int libraryRevision, CancellationToken cancel)
    {
        HashSet<string> matches; SearchDocument[] documents = cached ?? [];
        try
        {
            documents = cached ?? await Task.Run(() => { cancel.ThrowIfCancellationRequested(); return store.Media.Select(SearchDocument.From).ToArray(); }, cancel);
            matches = await mediaSearch.FindAsync(documents, term, cancel);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error)
        {
            Diagnostics.Log(store.DirectoryPath, error);
            matches = documents.Where(d => d.Text.Contains(term, StringComparison.OrdinalIgnoreCase)).Select(d => d.Id).ToHashSet();
        }
        if (cancel.IsCancellationRequested || shuttingDown || IsDisposed || revision != searchRevision) return;
        if (libraryRevision != store.Revision)
        {
            indexedQuery = ""; searchStoreRevision = -1;
            if (page is not "正在播放" and not "设置" and not "播放队列" and not "歌单与集合") Render();
            return;
        }
        searchDocuments = documents; searchDocumentsRevision = libraryRevision;
        searchMatches = matches; searchPending = false;
        if (page is not "正在播放" and not "设置" and not "播放队列" and not "歌单与集合") Render();
    }
    void EmptyLibraryResult(string headingText)
    {
        var body = new GlassCanvas { Dock = DockStyle.Fill };
        var heading = Caption(headingText, 18);
        heading.TextAlign = ContentAlignment.MiddleCenter;
        body.Controls.Add(heading);
        void Arrange()
        {
            if (body.IsDisposed || dpiReady && !dpiScaled.Contains(body)) return;
            float scale = dpiReady ? dpiFactor : 1; int P(float value) => (int)(value * scale);
            int width = Math.Max(P(80), Math.Min(P(600), body.ClientSize.Width - P(24)));
            int left = Math.Max(0, (body.ClientSize.Width - width) / 2);
            int top = Math.Max(P(24), body.ClientSize.Height / 3 - P(20));
            heading.SetBounds(left, top, width, P(40));
        }
        body.SizeChanged += (_, _) => Arrange();
        content.Controls.Add(body); body.BringToFront();
        layoutAlbumShelf = Arrange;
    }
    void Library()
    {
        using (UiPerformance.Measure("library_filter", page)) { var filtered = Filter(); visible = filtered as List<Media> ?? filtered.ToList(); }
        string mode = page == "音乐库" ? libraryMode : videoLibraryMode;
        // Every first-run library view uses the same introduction. A selected
        // filter or an empty result in an existing library is a different state.
        bool emptyLibrary = page is "音乐库" or "视频库"
            && search.Text.Trim().Length == 0 && selectedList == null
            && !LibrarySnapshot().Any(media => media.Kind == (page == "音乐库" ? "music" : "video"));
        FlowLayoutPanel actions;
        using (UiPerformance.Measure("library_actions", page)) actions = page is "音乐库" or "视频库" ? LibraryActions(mode, emptyLibrary) : Actions();
        if (page == "首页")
            foreach (var label in new[] { "全部", "继续观看", "最近播放", "最近添加" })
            {
                string value = label; var tab = (UiButton)Button(label, () => { group = value; Render(); }, 96, 44); tab.Style = UiButtonStyle.Tab; tab.Selected = group == label; actions.Controls.Add(tab);
            }
        else if (page is not ("音乐库" or "视频库") && visible.Count > 0) actions.Controls.Add(Button("播放当前列表", () => { if (visible.Count > 0) Play(visible[0]); }, 140, 40));
        if (emptyLibrary)
        {
            if (libraryTableHost != null) libraryTableHost.Visible = false;
            LibraryEmptyState();
            return;
        }
        if (page == "首页" && group == "全部" && search.Text.Trim().Length == 0) { HomeDashboard(); return; }
        if (visible.Count == 0)
        {
            if (libraryTableHost != null) libraryTableHost.Visible = false;
            var empty = search.Text.Trim().Length > 0
                ? searchPending ? "正在搜索…" : "暂无匹配内容"
                : selectedList != null ? "列表为空"
                : page == "收藏" ? "暂无收藏"
                : group != "全部" ? "暂无匹配内容"
                : "暂无内容";
            EmptyLibraryResult(empty);
            return;
        }
        if (page == "音乐库" && group == "全部" && selectedList == null && search.Text.Trim().Length == 0 && libraryMode is "专辑" or "歌手") { GroupedMusic(libraryMode); return; }
        if (page == "视频库" && useCards) { BuildVideoShelf(); return; }
        grid = libraryGrid;
        if (grid == null)
        {
        grid = new GlassDataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, BackgroundColor = BackColor, BorderStyle = BorderStyle.None, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, EnableHeadersVisualStyles = false, VirtualMode = true };
        grid.RowTemplate.Height = (int)(38 * DeviceDpi / 96f);
        foreach (var text in new[] { "标题", "歌手 / 分类", "专辑 / 文件夹", "时长", "状态" })
            grid.Columns.Add(new GlassGridTextColumn { Name = text, HeaderText = text });
        var availability = new GridFileAvailability(grid);
        libraryGrid = grid; libraryAvailability = availability;
        ThemeGrid(grid, availability);

        grid.CellValueNeeded += (_, e) => { if (e.RowIndex >= visible.Count) return; var m = visible[e.RowIndex]; bool? file = e.ColumnIndex == 4 ? availability.Get(m.Path, e.RowIndex) : null, cue = e.ColumnIndex != 4 ? null : m.Cue.Length == 0 ? true : availability.Get(m.Cue, e.RowIndex); e.Value = e.ColumnIndex switch { 0 => (m.Favorite ? "♥  " : "") + m.Title, 1 => m.Kind == "music" ? m.Artist : m.Category, 2 => m.Kind == "music" ? m.Album : Path.GetFileName(m.Folder), 3 => DurationText(m.Duration), _ => m.CueTrackMissing ? "CUE 曲目已变更" : file == false || cue == false ? "离线" : file != true || cue != true ? "检查中…" : m.Kind == "video" ? m.Watch + " " + Time(m.Position) : m.FormatNote.Contains("混合") ? "混合流（有损播放）" : m.Cue != "" ? "CUE · " + m.Track : "音乐" }; };
        grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Play(visible[e.RowIndex]); };
        grid.CellMouseDown += (_, e) => { if (e.RowIndex >= 0 && e.Button == MouseButtons.Right) { grid.CurrentCell = grid.Rows[e.RowIndex].Cells[0]; var menu = Menu(visible[e.RowIndex]); UiMenus.DisposeAfterClose(menu, this); menu.Show(Cursor.Position); } };
        }
        else using (UiPerformance.Measure("grid_theme", page)) ThemeGrid(grid, libraryAvailability!, initialize: false);
        using (UiPerformance.Measure("grid_row_resize", page, Math.Abs(grid.RowCount - visible.Count)))
        {
            // WinForms shrinks a nonzero RowCount by repeatedly RemoveAt.
            // A large music list -> a short video/search result otherwise
            // sends thousands of row/layout/UIA updates on the UI thread.
            // Clear has a native bulk path and retains this grid's HWND.
            if (grid.RowCount > 0 && grid.RowCount != visible.Count)
            {
                int selectedRow = grid.CurrentCell?.RowIndex ?? 0;
                int selectedColumn = grid.CurrentCell?.ColumnIndex ?? 0;
                grid.CurrentCell = null;
                grid.Rows.Clear();
                grid.RowCount = visible.Count;
                if (visible.Count > 0) grid.CurrentCell = grid.Rows[Math.Min(selectedRow, visible.Count - 1)].Cells[selectedColumn];
            }
            else grid.RowCount = visible.Count;
        }
        // Keep the native virtualized rows, but do not turn unused space under
        // a short album into an opaque slab across the entire glass page.
        var rows = grid;
        var tableHost = libraryTableHost;
        if (tableHost == null)
        {
            tableHost = libraryTableHost = new GlassCanvas { Dock = DockStyle.Fill };
            rows.Dock = DockStyle.Top;
            tableHost.Controls.Add(rows);
            tableHost.SizeChanged += (_, _) => ArrangeRows();
        }
        using (UiPerformance.Measure("grid_attach", page))
        {
            if (tableHost.Parent != content) content.Controls.Add(tableHost);
            tableHost.Visible = true;
            tableHost.BringToFront();
        }
        // The virtual grid and its HWND are reused across pages. RowCount can
        // stay unchanged even though every CellValueNeeded result and header
        // changed, so explicitly repaint the grid after attaching it again.
        rows.Invalidate(true);
        void ArrangeRows()
        {
            if (tableHost.IsDisposed || rows.IsDisposed || dpiReady && !dpiScaled.Contains(tableHost)) return;
            long desired = rows.ColumnHeadersHeight + (long)rows.RowTemplate.Height * rows.RowCount + 2;
            rows.Height = (int)Math.Min(Math.Max(1, tableHost.ClientSize.Height), desired);
        }
        layoutAlbumShelf = ArrangeRows;
        if (page is "音乐库" or "视频库" && mode == "文件夹") AddFolderTree();
    }
    bool useCards = true;
    int playbackRequest;
    static readonly SemaphoreSlim playbackProbeSlots = new(2, 2);
    CancellationTokenSource? playbackPreparation;
    void CancelPendingPlayback() { playbackRequest++; playbackPreparation?.Cancel(); }
    void RequestGpuWarmup()
    {
        if (mainFirstPaintObserved && Visible && !Disposing && !shuttingDown && !IsDisposed && player.Current?.Kind != "video") gpuWarmup.Request();
    }
    Task<bool> PrepareAndPlay(IReadOnlyList<Media?> source, int selectedIndex, bool resume, Func<string, bool>? fileExists = null) => PrepareAndPlayCore(source, selectedIndex, resume, fileExists, null);
    async Task<bool> PrepareAndPlayCore(IReadOnlyList<Media?> source, int selectedIndex, bool resume, Func<string, bool>? fileExists, Action<string>? start)
    {
        if (selectedIndex < 0 || selectedIndex >= source.Count || source[selectedIndex] is not { } selected)
            throw new FileNotFoundException("此项记录已失联，请重新添加对应文件。");
        if (selected.Kind == "video") RequestGpuWarmup();
        int request = ++playbackRequest;
        Diagnostics.TracePlayback("play requested", selected.Id, request);
        playbackPreparation?.Cancel();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        CancellationToken token = cancellation.Token;
        playbackPreparation = cancellation;
        bool Superseded() => request != playbackRequest || shuttingDown || restoringLibrary || IsDisposed;
        try
        {
            if (selected.CueTrackMissing)
                throw new FileNotFoundException("CUE 文件中已无此曲目，请检查 CUE 并重新扫描。", selected.Cue);
            var entries = source.ToArray();
            var selection = (selected.Path, selected.Cue, selected.Kind);
            var exists = fileExists ?? File.Exists;
            status.Text = "正在准备播放…"; messageUntil = DateTime.Now.AddSeconds(5);
            // Only the chosen occurrence must be available now. libmpv opens
            // the remaining files when reached, with its CUE load guard.
            var work = Task.Run(async () =>
            {
                await playbackProbeSlots.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    bool online = exists(selection.Path)
                        && (selection.Cue.Length == 0 || !token.IsCancellationRequested && exists(selection.Cue));
                    return online && !token.IsCancellationRequested ? WavPackInfo.Describe(selection.Path) : null;
                }
                finally { playbackProbeSlots.Release(); }
            }, token);
            string? format = await work.WaitAsync(token);
            if (Superseded()) return false;
            if (format == null || selection != (selected.Path, selected.Cue, selected.Kind))
                throw new FileNotFoundException("文件暂时离线或位置已变更。请检查后重新播放。", selection.Path);
            cancellation.CancelAfter(Timeout.InfiniteTimeSpan);
            await player.PrepareEngineAsync().WaitAsync(token);
            if (Superseded()) return false;
            if (selected.CueTrackMissing || selection != (selected.Path, selected.Cue, selected.Kind))
                throw new FileNotFoundException("文件位置或 CUE 曲目已变更。请检查后重新播放。", selection.Path);
            if (start == null) player.PlayAtPrepared(entries, selectedIndex, resume, format); else start(format);
            if (Superseded()) return false;
            if (status.Text == "正在准备播放…") messageUntil = DateTime.MinValue;
            albumPreview = null;
            if (selected.Kind == "video") page = "正在播放";
            Render();
            return true;
        }
        catch (OperationCanceledException) when (!Superseded()) { throw new IOException("媒体盘响应超时，请检查连接后重试。"); }
        catch (Exception) when (Superseded()) { return false; }
        finally { if (ReferenceEquals(playbackPreparation, cancellation)) playbackPreparation = null; }
    }
    void Play(Media m, bool? resume = null)
    {
        var source = visible.Count > 0 ? visible.Cast<Media?>().ToList() : [m];
        int index = source.IndexOf(m); if (index < 0) { source.Insert(0, m); index = 0; }
        _ = PlayWithFeedback(source, index, resume ?? (m.Kind == "music" && m.ResumeMusic));
    }
    async Task PlayWithFeedback(IReadOnlyList<Media?> entries, int index, bool resume)
    { try { await PrepareAndPlay(entries, index, resume); } catch (Exception e) { if (!shuttingDown && !IsDisposed) { status.Text = e.Message; messageUntil = DateTime.Now.AddSeconds(8); } } }
    new ContextMenuStrip Menu(Media m)
    {
        var menu = new ContextMenuStrip { BackColor = UiTheme.Surface, ForeColor = UiTheme.Ink };
        void Item(string text, Action a) => menu.Items.Add(text, null, (_, _) => Try(a));
        Item("从头播放", () => Play(m, false));
        Item("继续播放", () => Play(m, true));
        Item("下一首播放", () => Transport(() => player.Append(m, true), requiresEngine: false));
        Item("加入队列", () => Transport(() => player.Append(m, false), requiresEngine: false));
        Item(m.Favorite ? "取消收藏" : "收藏", () => { m.Favorite = !m.Favorite; store.SaveMetadata(m); if (page == "收藏" || grid == null) Render(); else { int row = visible.IndexOf(m); if (row >= 0) grid.InvalidateRow(row); } });
        if (m.Kind == "music")
        {
            var resume = new ToolStripMenuItem();
            void RefreshResume() { resume.Text = m.ResumeMusic ? "关闭此音频记忆进度" : "记忆此音频进度"; resume.Checked = m.ResumeMusic; }
            resume.Click += (_, _) => Try(() => { m.ResumeMusic = !m.ResumeMusic; store.SaveMetadata(m); RefreshResume(); });
            menu.Opening += (_, _) => RefreshResume();
            RefreshResume(); menu.Items.Add(resume);
        }
        foreach (var list in store.State.Lists.Where(l => l.Video == (m.Kind == "video")))
        {
            var l = list;
            Item("加入「" + l.Name + "」", () => { l.Items.Add(m.Id); store.SaveState(); });
        }
        if (selectedList != null)
        {
            Item("从此列表移除", () => { selectedList.Items.Remove(m.Id); store.SaveState(); Render(); });
        }
        if (m.Kind == "video")
        {
            foreach (var state in new[] { "未看", "未看完", "已看" })
            {
                string s = state;
                Item("标记：" + s, () => { store.SetWatchState(m, s); Render(); });
            }
            if (m.WatchManual) Item("恢复自动观看状态", () => { store.ResumeAutomaticWatchState(m); Render(); });
            Item(player.Current?.Id == m.Id ? "停止并重置此视频进度" : "重置此视频进度", () => { if (player.Current?.Id == m.Id) player.Clear(); store.ResetVideoProgress(m); Render(); });
            Item("设置分类", () => { var c = Prompt("分类（电影、剧集、纪录片、课程、家庭视频、音乐视频或自定义）", m.Category); if (c != null) { m.Category = c; store.SaveMetadata(m); Render(); } });
        }
        Item("更换本地封面", () => { using var f = new OpenFileDialog { Filter = "图片|*.jpg;*.jpeg;*.png" }; if (f.ShowDialog() == DialogResult.OK) { using (var check = Image.FromFile(f.FileName)) { } m.CoverPath = Path.GetFullPath(f.FileName); store.SaveMetadata(m); footerMediaId = null; Render(); UpdatePlayerChrome(); } });
        if (m.CoverPath.Length > 0) Item("使用自动封面", () => { m.CoverPath = ""; store.SaveMetadata(m); footerMediaId = null; Render(); UpdatePlayerChrome(); });
        Item("音视频详情", () => MessageBox.Show($"{m.Title}\n{m.Path}\n\n源文件：{m.Codec}\n格式说明：{m.FormatNote}\n采样率：{(m.Rate > 0 ? m.Rate.ToString() : "未知")} Hz\n位深：{(m.Bits > 0 ? m.Bits.ToString() : "未知")}\n声道：{(m.Channels > 0 ? m.Channels.ToString() : "未知")}\n画面：{m.Width} × {m.Height}\n大小：{m.Size:N0} 字节\nCUE 起点：{m.Start:F3} 秒\n\n当前设备输出（仅正在播放时有效）：\n{player.ReadEngineProperty("audio-out-params")}\n设备：{player.ReadEngineProperty("audio-device")}", "媒体详情"));
        Item("重新定位文件", () => { using var f = new OpenFileDialog(); if (f.ShowDialog() == DialogResult.OK) { store.RelinkFile(m, f.FileName); Render(); } });
        return menu;
    }
    void Lists() => BuildCollectionsPage();
    void NewList(bool videoList)
    {
        var n = Prompt(videoList ? "集合名称" : "歌单名称", "");
        if (!string.IsNullOrWhiteSpace(n))
        {
            store.State.Lists.Add(new MediaList { Name = n, Video = videoList });
            store.SaveState();
            Render();
        }
    }
    void EditList(MediaList l)
    {
        using var dialog = new Form { Icon = Brand.AppIcon(), Font = Font, Text = l.Name, AutoScaleMode = AutoScaleMode.None, Size = new Size((int)(620 * dpiFactor), (int)(560 * dpiFactor)), StartPosition = FormStartPosition.CenterParent, BackColor = BackColor, ForeColor = ForeColor };
        var list = new UiListBox { Dock = DockStyle.Fill, AllowDrop = true, BackColor = UiTheme.Surface, ForeColor = ForeColor };
        var availability = new GridFileAvailability(list);
        list.RowDetail = (index, row) =>
        {
            if (row.Media.CueTrackMissing) return "CUE 曲目已变更";
            bool? online = availability.Get(row.Media.Path, index);
            bool? cue = row.Media.Cue.Length == 0 ? true : availability.Get(row.Media.Cue, index);
            return online == false || cue == false ? "离线" : row.Detail;
        };
        void Refresh(int selection = -1)
        {
            list.BeginUpdate();
            try
            {
                var lookup = store.Media.ToDictionary(media => media.Id);
                list.Items.Clear();
                foreach (var id in l.Items)
                    list.Items.Add(lookup.TryGetValue(id, out var media) ? new MediaRow(media, media.Artist) : "已失联记录");
                if (list.Items.Count > 0) list.SelectedIndex = Math.Clamp(selection, 0, list.Items.Count - 1);
            }
            finally { list.EndUpdate(); }
        }
        Refresh();
        dialog.Padding = new Padding((int)(20 * dpiFactor));
        var buttons = new GlassFlowPanel { Dock = DockStyle.Bottom, Height = 96, Padding = new Padding(0, 12, 0, 0) };
        int drag = -1; Point dragOrigin = Point.Empty;
        string dragToken = Guid.NewGuid().ToString("N"); const string dragFormat = "HARMOVUE.PlaylistRow";
        list.MouseDown += (_, e) => { drag = e.Button == MouseButtons.Left ? list.IndexFromPoint(e.Location) : -1; dragOrigin = e.Location; };
        list.MouseMove += (_, e) =>
        {
            var threshold = new Rectangle(dragOrigin.X - SystemInformation.DragSize.Width / 2, dragOrigin.Y - SystemInformation.DragSize.Height / 2, SystemInformation.DragSize.Width, SystemInformation.DragSize.Height);
            if (e.Button != MouseButtons.Left || drag < 0 || threshold.Contains(e.Location)) return;
            try { list.DoDragDrop(new DataObject(dragFormat, dragToken), DragDropEffects.Move); }
            finally { drag = -1; }
        };
        void DragEffect(object? sender, DragEventArgs e) => e.Effect = drag >= 0 && Equals(e.Data?.GetData(dragFormat), dragToken) ? DragDropEffects.Move : DragDropEffects.None;
        list.DragEnter += DragEffect; list.DragOver += DragEffect;
        list.DragDrop += (_, e) =>
        {
            if (drag < 0 || drag >= l.Items.Count || !Equals(e.Data?.GetData(dragFormat), dragToken)) return;
            Point point = list.PointToClient(new Point(e.X, e.Y));
            if (!list.ClientRectangle.Contains(point)) return;
            int target = list.IndexFromPoint(point);
            if (target < 0) target = l.Items.Count - 1;
            if (target == drag) return;
            var id = l.Items[drag]; l.Items.RemoveAt(drag); l.Items.Insert(target, id);
            store.SaveState(); Refresh(target);
        };
        async void PlaySelection()
        {
            if (list.SelectedIndex < 0) return;
            var lookup = store.Media.ToDictionary(media => media.Id);
            var entries = l.Items.Select(id => lookup.GetValueOrDefault(id)).ToArray();
            var selected = entries[list.SelectedIndex];
            try { if (!await PrepareAndPlay(entries, list.SelectedIndex, selected is { Kind: "music", ResumeMusic: true } || selected is { Kind: "video", Position: > 0 })) return; }
            catch (Exception error)
            {
                MessageBox.Show(dialog, error.Message, "无法播放", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!dialog.IsDisposed) dialog.Close();
        }
        list.DoubleClick += (_, _) => Try(PlaySelection);
        buttons.Controls.Add(Button("播放", PlaySelection));
        buttons.Controls.Add(Button("改名", () => { var n = Prompt("名称", l.Name); if (n != null) { l.Name = n; store.SaveState(); dialog.Text = n; } }));
        buttons.Controls.Add(Button("移除所选", () => { int selected = list.SelectedIndex; if (selected >= 0) { l.Items.RemoveAt(selected); store.SaveState(); Refresh(selected); } }));
        buttons.Controls.Add(Button("导出 M3U8", () => ExportList(l)));
        buttons.Controls.Add(Button("删除列表", () => { store.State.Lists.Remove(l); store.SaveState(); dialog.Close(); Render(); }));
        dialog.Controls.Add(list);
        dialog.Controls.Add(buttons);
        foreach (Control child in dialog.Controls) child.Scale(new SizeF(dpiFactor, dpiFactor));
        dialog.HandleCreated += (_, _) => UiTheme.WindowChrome(dialog);
        dialog.ShowDialog(this);
    }
    void ExportList(MediaList l)
    {
        using var f = new SaveFileDialog { Filter = "M3U8|*.m3u8", FileName = l.Name + ".m3u8" };
        if (f.ShowDialog() != DialogResult.OK)
            return;
        PlaylistFiles.Export(store, l, f.FileName);
        status.Text = "已导出；M3U8 不保留 CUE 虚拟曲目边界。完整数据请使用备份。";
    }
    void ImportList()
    {
        using var f = new OpenFileDialog { Filter = "列表|*.m3u;*.m3u8" };
        if (f.ShowDialog() != DialogResult.OK)
            return;
        var result = PlaylistFiles.Import(store, f.FileName);
        Render();
        status.Text = $"已导入 {result.Lists.Sum(l => l.Items.Count)} 项，音乐与视频分别保存；离线 {result.Offline} 项，跳过 {result.Skipped} 项（网络地址、不支持或无效路径）。";
    }
    void RefreshQueue()
    {
        railQueueDirty = true;
        // Most playback changes happen while this full list is detached. Do
        // not populate thousands of native ListBox items nobody can see.
        if (queue.Parent == null && musicQueue is not { IsDisposed: false }) return;
        using var measurement = UiPerformance.Measure("queue_rebuild", count: player.Session.Queue.Count);
        queue.BeginUpdate();
        musicQueue?.BeginUpdate();
        try
        {
        queue.Items.Clear();
        foreach (var id in player.Session.Queue)
        {
            var media = store.FindById(id);
            queue.Items.Add(media == null ? "离线记录" : new MediaRow(media, media.Kind == "music" ? media.Artist.Length > 0 ? media.Artist : "本地音乐 · " + DurationText(media.Duration) : media.Watch + " · " + DurationText(media.Duration)));
        }
        if (queue.Items.Count > player.Session.Index)
            queue.SelectedIndex = player.Session.Index;
        if (musicQueue is { IsDisposed: false })
        {
            musicQueue.Items.Clear();
            foreach (var item in queue.Items) musicQueue.Items.Add(item);
            if (musicQueue.Items.Count > player.Session.Index) musicQueue.SelectedIndex = player.Session.Index;
        }
        }
        finally { musicQueue?.EndUpdate(); queue.EndUpdate(); }
    }
    void QueuePlay(object? sender, EventArgs e)
    {
        if (queue.SelectedIndex >= 0)
            SelectQueue(queue.SelectedIndex);
    }
    string? lastNowId; ListBox? musicQueue;
    void NowPlaying() => BuildNowPlaying();
    Label? lyrics; List<(double Time, string Text)> lines = [];
    CancellationTokenSource? lyricsRequest;
    async void ReadLyrics(Media m)
    {
        lyricsRequest?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(coverLifetime.Token);
        request.CancelAfter(TimeSpan.FromSeconds(8)); lyricsRequest = request;
        var target = lyrics; string id = m.Id, path = m.Path;
        lines = [];
        if (target is null) return;
        target.Text = "";
        try
        {
            var result = await LocalLyrics.ReadAsync(path, request.Token);
            if (request.IsCancellationRequested || target.IsDisposed || !ReferenceEquals(lyrics, target) || player.Current?.Id != id || player.Current.Path != path) return;
            lines = result.Lines;
            target.Text = lines.Count == 0 ? result.Text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "" : lines.LastOrDefault(l => l.Time <= player.Position + player.CurrentStart).Text ?? "";
        }
        catch (OperationCanceledException) { if (!target.IsDisposed && ReferenceEquals(lyrics, target) && ReferenceEquals(lyricsRequest, request)) target.Text = "歌词暂时不可用"; }
        catch (Exception e) { Diagnostics.Log(store.DirectoryPath, e); if (!target.IsDisposed && ReferenceEquals(lyrics, target) && ReferenceEquals(lyricsRequest, request)) target.Text = "歌词暂时不可用"; }
        finally { if (ReferenceEquals(lyricsRequest, request)) lyricsRequest = null; }
    }
    void ChooseTrack(string type, string property)
    {
        using var json = JsonDocument.Parse(player.Engine.Get("track-list"));
        var list = json.RootElement.EnumerateArray().Where(x => x.GetProperty("type").GetString() == type).ToArray();
        var options=new List<Choice>{new("auto","自动选择")};if(type=="sub")options.Add(new("no","关闭字幕"));
        foreach(var track in list){string Field(string name)=>track.TryGetProperty(name,out var v)?v.ToString():"";options.Add(new(Field("id"),string.Join(" · ",new[]{Field("id"),Field("title"),Field("lang"),Field("codec"),Field("external")=="True"?"外挂":""}.Where(v=>v.Length>0))));}
        var id=Choose(type=="sub"?"选择字幕":"选择音轨",options,player.Engine.Get(property));
        if (id != null)
        {
            player.SelectTrack(property, id);
            if (player.Current is Media m)
            {
                if (property == "aid")
                    m.Audio = id;
                else
                    m.Subtitle = id;
                store.SaveMetadata(m);
            }
        }
    }
    void Chapters()
    {
        string json = player.Engine.Get("chapter-list");
        using var doc = JsonDocument.Parse(json);
        var chapters = doc.RootElement.EnumerateArray().ToArray();
        var n=Choose("章节",chapters.Select((v,i)=>new Choice(i.ToString(),Time(v.GetProperty("time").GetDouble())+" · "+(v.TryGetProperty("title",out var title)?title.GetString():"章节 "+(i+1)))),player.Engine.Get("chapter"));
        if (int.TryParse(n, out int index) && index >= 0 && index < chapters.Length)
            player.Engine.Set("chapter", index);
    }
    sealed record Choice(string Id,string Label){public override string ToString()=>Label;}
    string? Choose(string title, IEnumerable<Choice> options, string selected) => ChooseSetting(title, options, selected);
    async void ChooseDevice()
    {
        try
        {
            await player.PrepareEngineAsync();
            if (shuttingDown || IsDisposed || restoringLibrary) return;
            using var json = JsonDocument.Parse(player.Engine.Get("audio-device-list"));
            var options = json.RootElement.EnumerateArray().Select(d => new Choice(d.GetProperty("name").GetString()!,
                d.GetProperty("name").GetString() == "auto" ? "跟随 Windows 默认输出设备" : d.GetProperty("description").GetString() ?? "音频设备"));
            var name = Choose("音频输出设备", options, store.State.Device);
            if (name == null) return;
            player.Engine.Set("pause", "yes");
            player.Engine.Set("audio-device", name);
            store.State.Device = name;
            store.SaveState();
            status.Text = "输出设备已选择，点击播放恢复。";
        }
        catch (Exception error)
        {
            if (!shuttingDown && !IsDisposed) status.Text = error.Message;
        }
    }
    void Screenshot()
    {
        using var f = new SaveFileDialog { Filter = "PNG|*.png", InitialDirectory = store.DirectoryPath, FileName = "HARMOVUE-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png" };
        if (f.ShowDialog() != DialogResult.OK)
            return;
        var include = MessageBox.Show("截图包含字幕？", "截图", MessageBoxButtons.YesNo) == DialogResult.Yes;
        player.Engine.Command("screenshot-to-file", f.FileName, include ? "subtitles" : "video");
    }
    void Fullscreen()
    {
        if (player.Current?.Kind == "video") { ToggleVideoFullscreen(); return; }
        if (!full)
        {
            full = true;
            priorBorder = FormBorderStyle;
            priorBounds = Bounds;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            Bounds = Screen.FromControl(this).Bounds;
            nav.Visible = false;
            toolbar.Visible = false;
            if (player.Current?.Kind == "video")
            {
                if (page != "正在播放")
                {
                    page = "正在播放";
                    Render();
                }
                foreach (Control c in content.Controls)
                    if (c != stage)
                        c.Visible = false;
                content.Padding = Padding.Empty;
                stage.Dock = DockStyle.Fill;
            }
        }
        else
        {
            full = false;
            stage.Dock = DockStyle.Top;
            content.Padding = new Padding((int)(32 * dpiFactor), 0, (int)(32 * dpiFactor), (int)(24 * dpiFactor));
            FormBorderStyle = priorBorder;
            Bounds = priorBounds;
            nav.Visible = true;
            toolbar.Visible = true;
            Render();
        }
        LayoutChrome();
    }
    void MiniWindow()
    {
        if (immersiveVideo) ExitVideoPresentation();
        miniActive = true;
        bool videoMode=player.Current?.Kind=="video";
        var mini = new Form { Text = "HARMOVUE · 迷你播放器", Icon = Brand.AppIcon(), AutoScaleMode=AutoScaleMode.None,Size = new Size((int)(600*dpiFactor), (int)((videoMode?400:170)*dpiFactor)), TopMost = true, BackColor = BackColor, ForeColor = ForeColor, Font = Font, StartPosition = FormStartPosition.CenterScreen };
        var label = new GlassLabel { Dock = DockStyle.Top, Height = 55, AutoEllipsis = true };
        var actions = new GlassFlowPanel { Dock = videoMode?DockStyle.Bottom:DockStyle.Fill,Height=50 };
        actions.Controls.Add(Button("上一首", () => Transport(() => player.Next(true))));
        actions.Controls.Add(Button("播放 / 暂停", TogglePlayback));
        actions.Controls.Add(Button("下一首", () => Transport(() => player.Next())));
        actions.Controls.Add(Button("返回主窗口", () => mini.Close()));
        mini.Controls.Add(actions);
        mini.Controls.Add(label);
        foreach(Control c in mini.Controls)c.Scale(new SizeF(dpiFactor,dpiFactor));
        if(videoMode){stage.Controls.Remove(video);mini.Controls.Add(video);video.BringToFront();mini.FormClosing+=(_,_)=>{mini.Controls.Remove(video);stage.Controls.Add(video);};}
        var refresh = new System.Windows.Forms.Timer { Interval = 400 };
        refresh.Tick += (_, _) => label.Text = (player.Current?.Title ?? "尚未播放") + "\n" + Time(player.Current?.Position ?? 0);
        mini.FormClosed += (_, _) => { refresh.Dispose(); miniActive = false; Show(); Activate(); SyncVideoPresentation(); };
        refresh.Start();
        mini.Show();
        Hide();
    }
    void Settings() => BuildSettingsPage();
    void OpenFiles()
    {
        using var f = new OpenFileDialog { Multiselect = true, Filter = "影音与 CUE|" + string.Join(';', Scanner.Extensions.Select(s => "*" + s)) + ";*.cue|所有文件|*.*" };
        if (f.ShowDialog() == DialogResult.OK)
            _ = OpenPaths(f.FileNames);
    }
    public async Task OpenPaths(string[] paths)
    {
        Diagnostics.TracePlayback("file open requested");
        if (shuttingDown || restoringLibrary) return;
        openingFiles?.Cancel(); openingFiles?.Dispose();
        using var request = new CancellationTokenSource(); openingFiles = request;
        CancellationToken cancel = request.Token;
        int generation = scanGeneration;
        try
        {
            var opened = await Task.Run(() =>
            {
                var media = new List<Media>(); var folders = new List<string>();
                foreach (var path in paths)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (Directory.Exists(path)) { folders.Add(path); continue; }
                    if (!File.Exists(path)) continue;
                    media.AddRange(Path.GetExtension(path).Equals(".cue", StringComparison.OrdinalIgnoreCase) ? Scanner.Cue(path) : [Scanner.Read(path)]);
                }
                return (Media: media, Folders: folders);
            }, cancel);
            if (cancel.IsCancellationRequested || shuttingDown || !ReferenceEquals(openingFiles, request)) return;
            var list = opened.Media;
            store.Upsert(list.Where(m => m.Cue.Length == 0));
            foreach (var cue in list.Where(m => m.Cue.Length != 0).GroupBy(m => m.Cue, StringComparer.OrdinalIgnoreCase))
                store.UpsertCue(cue.Key, cue.DistinctBy(m => m.Key));
            var byKey = store.Media.ToDictionary(m => m.Key);
            visible = list.Select(m => byKey[m.Key]).ToList();
            if (visible.Count > 0)
            {
                if (visible[0].Kind == "music") { page = "音乐库"; libraryMode = "专辑"; }
                Play(visible[0]);
            }
            else
                Render();
            // Explicit files start before any directory scan supplied in the
            // same drop/open request. New opens replace pending metadata work;
            // a scan already started remains an independent library operation.
            foreach (string folder in opened.Folders)
            {
                if (cancel.IsCancellationRequested || shuttingDown || restoringLibrary || generation != scanGeneration) break;
                await ScanRoot(folder);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!shuttingDown && ReferenceEquals(openingFiles, request)) status.Text = e.Message; }
        finally { if (ReferenceEquals(openingFiles, request)) openingFiles = null; }
    }
    void AddRoot()
    {
        using var f = new FolderBrowserDialog { Description = "选择需要管理的媒体目录（原文件只读）" };
        if (f.ShowDialog() == DialogResult.OK)
            _ = ScanRoot(f.SelectedPath);
    }
    async Task ScanRoot(string root)
    {
        if (shuttingDown || restoringLibrary) return;
        if (scan != null)
        {
            status.Text = "正在扫描，完成或取消后再添加目录。";
            return;
        }
        if (!store.State.Roots.Any(r => r.Path.Equals(root, StringComparison.OrdinalIgnoreCase)))
            store.State.Roots.Add(new Root { Path = root });
        scan = new();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scanCompletion = completion.Task;
        try
        {
            status.Text = "扫描中…";
            scanProgress.Take();
            using var availability = CancellationTokenSource.CreateLinkedTokenSource(scan.Token);
            availability.CancelAfter(TimeSpan.FromSeconds(8));
            if (!await BackgroundPaths.DirectoryExistsAsync(root, availability.Token))
                throw new DirectoryNotFoundException("媒体目录暂时离线。");
            scanWork = Scanner.Scan(store, root, scanProgress, scan.Token);
            await scanWork;
            status.Text = $"扫描完成 · {store.Media.Count} 项";
        }
        catch (OperationCanceledException) { status.Text = "扫描已取消，已索引数据保留。重新扫描可继续。"; }
        catch (Exception e) { status.Text = "扫描失败：" + e.Message; }
        finally
        {
            scan.Dispose(); scan = null;
            try { if (!shuttingDown && !restoringLibrary) { UpdateWatchers(); Render(); } }
            finally { completion.TrySetResult(); }
        }
    }
    void CancelScans() { scanGeneration++; scan?.Cancel(); }
    async Task ScanAll()
    {
        if (scan != null || shuttingDown || restoringLibrary) return;
        int generation = scanGeneration;
        foreach (var r in store.State.Roots.Where(r => !r.Paused).ToArray())
        {
            if (shuttingDown || restoringLibrary || generation != scanGeneration) break;
            await ScanRoot(r.Path);
        }
    }
    static Bitmap DisplayCover(Image source)
    {
        double ratio = Math.Min(1, 640d / Math.Max(source.Width, source.Height));
        var image = new Bitmap(Math.Max(1, (int)(source.Width * ratio)), Math.Max(1, (int)(source.Height * ratio)));
        using var graphics = Graphics.FromImage(image);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, 0, 0, image.Width, image.Height);
        return image;
    }
    void LoadCover(Media m, PictureBox box, Action? completed = null)
    {
        using var measurement = UiPerformance.Measure("load_cover_schedule", m.Kind);
        if (shuttingDown || box.IsDisposed) return;
        // Snapshot only the data needed for image I/O; no Media or Control is
        // accessed by the worker. A footer request survives browse-page changes.
        var source = new CoverSource(m.Id, m.Path, m.Folder, m.Kind, Path.Combine(store.DirectoryPath, "cache"), m.CoverPath);
        bool persistent = ReferenceEquals(box, footerCover) || ReferenceEquals(box, railCover);
        var request = CancellationTokenSource.CreateLinkedTokenSource(coverLifetime.Token, persistent ? CancellationToken.None : thumbs.Token);
        if (coverRequests.TryGetValue(box, out var previous)) previous.Cancel();
        coverRequests[box] = request;
        EventHandler disposed = (_, _) => request.Cancel();
        box.Disposed += disposed;
        _ = CompleteCover(source, box, request, disposed, completed);
    }
    async Task CompleteCover(CoverSource source, PictureBox box, CancellationTokenSource request, EventHandler disposed, Action? completed)
    {
        Bitmap? image = null;
        try
        {
            image = await Task.Run(() => ReadCover(source, request.Token), request.Token);
            // This continuation runs on the UI context captured at scheduling.
            // A disposed page, superseded track or shutdown must never receive it.
            if (!request.IsCancellationRequested && !shuttingDown && !box.IsDisposed
                && (!ReferenceEquals(box, footerCover) || footerMediaId == source.Id)
                && coverRequests.TryGetValue(box, out var active) && ReferenceEquals(active, request))
            {
                Image? old = box.Image;
                box.Image = image; image = null;
                old?.Dispose();
                completed?.Invoke();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { UiPerformance.Record("cover_failure", 0, error.GetType().Name); }
        finally
        {
            image?.Dispose();
            box.Disposed -= disposed;
            if (coverRequests.TryGetValue(box, out var active) && ReferenceEquals(active, request)) coverRequests.Remove(box);
            request.Dispose();
        }
    }
    static async Task<Bitmap?> ReadCover(CoverSource source, CancellationToken cancel)
    {
        using (UiPerformance.Measure("cover_queue", source.Kind)) await coverReadGate.WaitAsync(cancel).ConfigureAwait(false);
        Bitmap? image = null;
        try
        {
            using var measurement = UiPerformance.Measure("cover_worker", source.Kind);
            cancel.ThrowIfCancellationRequested();
            string cache = Path.Combine(source.CacheDirectory, source.Id + ".jpg");
            string? path = File.Exists(source.CoverPath) ? source.CoverPath : File.Exists(cache) ? cache : new[] { Path.ChangeExtension(source.Path, ".jpg"), Path.Combine(source.Folder, "cover.jpg"), Path.Combine(source.Folder, "folder.jpg"), Path.Combine(source.Folder, "poster.jpg") }.FirstOrDefault(File.Exists);
            if (path == null && source.Kind == "video" && File.Exists(source.Path))
            {
                await Enrichment.Thumbnail(new Media { Id = source.Id, Path = source.Path, Kind = source.Kind }, source.CacheDirectory, cancel).ConfigureAwait(false);
                if (File.Exists(cache)) path = cache;
            }
            cancel.ThrowIfCancellationRequested();
            if (path != null)
            {
                using var original = Image.FromFile(path);
                image = DisplayCover(original);
            }
            else if (source.Kind == "music" && File.Exists(source.Path))
            {
                using var file = TagLib.File.Create(source.Path);
                if (file.Tag.Pictures.Length > 0)
                {
                    using var stream = new MemoryStream(file.Tag.Pictures[0].Data.Data);
                    using var original = Image.FromStream(stream);
                    image = DisplayCover(original);
                }
            }
            cancel.ThrowIfCancellationRequested();
            Bitmap? ready = image; image = null; return ready;
        }
        finally
        {
            // This image was never handed to a Control. Release it here if a
            // canceled/disposed request aborts before returning to the UI.
            image?.Dispose(); coverReadGate.Release();
        }
    }
    void Tick()
    {
        uiPerformance.Tick();
        using var measurement = UiPerformance.Measure("tick_sync", page);
        if (scan != null && scanProgress.Take() is { } progress) status.Text = "扫描：" + Path.GetFileName(progress);
        if(++scanTicks%3000==0 && scan==null && store.State.PeriodicScan && !shuttingDown)_=ScanAll();
        if (!shuttingDown && watcher.RequestRetry()) { status.Text = "部分目录监听暂不可用，正在重试。"; messageUntil = DateTime.Now.AddSeconds(4); }
        if (watcher.Due && scan == null && !shuttingDown) { watcher.Acknowledge(); _ = ScanAll(); }
        using (UiPerformance.Measure("playback_tick")) player.Tick();
        using (UiPerformance.Measure("player_chrome")) UpdatePlayerChrome();
        AutoHideVideoControls();
        if (subtitleAppearanceButton is { IsDisposed: false }) subtitleAppearanceButton.Enabled = !BitmapSubtitleSelected();
        using (UiPerformance.Measure("system_integration")) integration?.Update();
        var m = player.Current;
        if (m != null)
        {
            double duration = player.CurrentDuration;
            if (!dragging && duration > 0)
                seek.Value = Math.Clamp((int)(player.Position / duration * 10000), 0, 10000);
            clock.Text = Time(player.Position) + " / " + Time(duration);
            if (lyrics != null && !lyrics.IsDisposed && lines.Count > 0)
                lyrics.Text = lines.LastOrDefault(l => l.Time <= player.Position + player.CurrentStart).Text ?? "";
        }
        status.Visible = !immersiveVideo && (scan != null || DateTime.Now <= messageUntil);
    }
    static string DurationText(double seconds) => double.IsFinite(seconds) && seconds > 0 ? Time(seconds) : "—";
    static string Time(double s) => TimeSpan.FromSeconds(Math.Max(0, double.IsFinite(s) ? s : 0)).ToString(s >= 3600 ? @"hh\:mm\:ss" : @"mm\:ss");
    string outputDevice = "", outputLabel = ""; DateTime outputChecked;
    string OutputDescription()
    {
        string device = player.Engine.Get("audio-device");
        if (device is "auto" or "") return "默认输出：" + (integration?.DefaultOutputName ?? "系统默认设备");
        if (device == outputDevice && DateTime.UtcNow - outputChecked < TimeSpan.FromSeconds(2)) return outputLabel;
        outputDevice = device; outputChecked = DateTime.UtcNow; outputLabel = "所选输出设备暂不可用";
        string json = player.Engine.Get("audio-device-list");
        if (!string.IsNullOrEmpty(json))
        {
            using var list = JsonDocument.Parse(json);
            foreach (var item in list.RootElement.EnumerateArray())
                if (item.GetProperty("name").GetString() == device) { outputLabel = item.GetProperty("description").GetString() ?? "固定输出设备"; break; }
        }
        return outputLabel;
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.F) && !immersiveVideo) { searchExpanded = true; LayoutChrome(); search.Focus(); search.SelectAll(); return true; }
        if (keyData == Keys.Escape && queueDrawerOpen && !immersiveVideo) { ToggleQueueDrawer(); return true; }
        if (keyData == Keys.Escape && immersiveVideo && !full) { Navigate("视频库"); return true; }
        if (keyData == Keys.Escape && full)
        {
            Fullscreen();
            return true;
        }
        if (cards is VirtualMediaShelf { ContainsFocus: true } && keyData is Keys.Space or Keys.Enter or Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown)
            return base.ProcessCmdKey(ref msg, keyData);
        var binding = store.State.Shortcuts.FirstOrDefault(b => Enum.TryParse<Keys>(b.Value, out var k) && k == keyData);
        if (binding.Key == null)
            return base.ProcessCmdKey(ref msg, keyData);
        if (ActiveControl is TextBox && keyData is Keys.Space or Keys.Left or Keys.Right)
            return base.ProcessCmdKey(ref msg, keyData);
        Try(() => { switch (binding.Key) { case "播放暂停": TogglePlayback(); break; case "下一首": Transport(() => player.Next()); break; case "上一首": Transport(() => player.Next(true)); break; case "前进5秒": SeekPlayback((player.Current?.Position ?? 0) + 5); break; case "后退5秒": SeekPlayback(Math.Max(0, (player.Current?.Position ?? 0) - 5)); break; case "全屏": Fullscreen(); break; } });
        return true;
    }
    void EditShortcuts() => EditShortcutPreferences();
    bool BitmapSubtitleSelected()
    {
        string tracks = player.ReadEngineProperty("track-list");
        if (string.IsNullOrWhiteSpace(tracks)) return false;
        using var doc = JsonDocument.Parse(tracks);
        string codec = doc.RootElement.EnumerateArray().Where(t => t.GetProperty("type").GetString() == "sub" && t.TryGetProperty("selected", out var selected) && selected.GetBoolean()).Select(t => t.TryGetProperty("codec", out var c) ? c.GetString() ?? "" : "").FirstOrDefault() ?? "";
        return codec is "hdmv_pgs_subtitle" or "dvd_subtitle" or "dvb_subtitle" or "xsub";
    }
    void SubtitleAppearance()
    {
        if (BitmapSubtitleSelected())
        {
            MessageBox.Show("当前为图形字幕，不能修改字体；可使用字幕时间同步控制。", "字幕外观");
            return;
        }
        var value = Prompt("输入：字号;垂直位置0–100;是否主动覆盖ASS样式 true/false\n默认尊重ASS原样式及字体。", $"{store.State.SubtitleSize};{store.State.SubtitlePosition};{store.State.OverrideAss}");
        if (value == null)
            return;
        var fields = value.Split(';');
        if (fields.Length != 3 || !double.TryParse(fields[0], out var size) || size < 12 || size > 120 || !double.TryParse(fields[1], out var position) || position < 0 || position > 100 || !bool.TryParse(fields[2], out var ass))
            throw new FormatException("请输入有效的字号、位置和 true/false");
        store.State.SubtitleSize = size;
        store.State.SubtitlePosition = position;
        store.State.OverrideAss = ass;
        player.SetEngineOption("sub-font-size", size);
        ApplyVideoSubtitlePosition();
        player.SetEngineOption("sub-ass-override", ass ? "force" : "no");
        store.Save();
    }
    string? Prompt(string text, string value) => PromptSetting(text, value);
    protected override void WndProc(ref Message m)
    {
        if (UiAccessibility.Exiting && m.Msg == 0x003D) { m.Result = 0; return; }
        if (m.Msg == 0x0319 && player != null && integration == null)
        {
            int cmd = (int)((m.LParam.ToInt64() >> 16) & 0xffff);
            RequestMediaCommand(cmd switch { 14 => "toggle", 11 => "next", 12 => "previous", 13 => "pause", _ => "" });
        }
        base.WndProc(ref m);
    }
}


















