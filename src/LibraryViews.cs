// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

namespace LocalMedia;

public sealed partial class MainWindow
{
    string libraryMode = "专辑", videoLibraryMode = "全部视频";
    readonly DirectoryMonitor watcher = new();
    readonly NotifyIcon tray = new() { Text = "HARMOVUE", Icon = Brand.AppIcon() };
    bool exitRequested;
    void ChooseSubtitleEncoding()
    {
        if (player.Current is not Media media) return;
        string? encoding = Choose("外挂字幕编码（对此文件保存）", [new Choice("gb18030", "简体中文 GB18030 / GBK（UTF-8 优先）"), new Choice("auto", "自动检测其他编码"), new Choice("utf-8", "UTF-8"), new Choice("big5", "繁体中文 Big5（UTF-8 优先）"), new Choice("shift_jis", "日文 Shift-JIS（UTF-8 优先）")], media.SubCodepage);
        if (encoding == null) return;
        media.SubCodepage = encoding; store.SaveMetadata(media); player.Engine.Set("sub-codepage", encoding);
        player.ReloadSubtitles();
        status.Text = "外挂字幕编码已对此文件保存；内嵌字幕仍由容器处理。";
        messageUntil = DateTime.Now.AddSeconds(8);
    }
    static string AlbumKey(Media m) => string.IsNullOrWhiteSpace(m.Album) ? "folder:" + m.Folder : (string.IsNullOrWhiteSpace(m.AlbumArtist) ? m.Artist : m.AlbumArtist) + "\u001f" + m.Album;

    void InitializeLibraryTools()
    {
        var menu = new ContextMenuStrip();
        void RestoreWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); tray.Visible = false; }
        menu.Items.Add("打开 HARMOVUE", null, (_, _) => RestoreWindow());
        menu.Items.Add("播放 / 暂停", null, (_, _) => TogglePlayback());
        menu.Items.Add("退出", null, (_, _) => { exitRequested = true; Close(); });
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => RestoreWindow();
        UpdateWatchers();
        content.Resize += (_, _) => { if (!full && page == "正在播放" && player.Current?.Kind == "video" && stage.Parent == content) stage.Height = Math.Max((int)(150 * dpiFactor), content.Height - (int)(360 * dpiFactor)); };
    }

    void UpdateWatchers() => watcher.Configure(store.State.WatchFolders
        ? store.State.Roots.Where(r => !r.Paused).Select(r => r.Path) : []);

    void HomeDashboard() => BuildHomeDashboard();

    void AboutBrand()
    {
        using var dialog = new SettingsDialog("关于 HARMOVUE", "音乐与视频，在同一个地方。", new Size(620, 420), dpiFactor, Font, "关闭");
        using var logo = Brand.Logo();
        var details = new Label { Dock = DockStyle.Fill, Padding = new Padding(0, (int)(20 * dpiFactor), 0, 0), Font = Font, ForeColor = UiTheme.Muted,
            Text = $"{ProductVersionLabel} · by IDEORIN\nlibmpv 播放内核 · 媒体原文件默认只读\n\n兼容性与未验证范围见随附说明。\n升级保留现有 LocalMedia 数据。" };
        var image = new PictureBox { Image = logo, Dock = DockStyle.Top, Height = (int)(60 * dpiFactor), SizeMode = PictureBoxSizeMode.Zoom, BackColor = UiTheme.Surface };
        dialog.Body.Controls.Add(details); dialog.Body.Controls.Add(image);
        dialog.ShowDialog(this);
    }

    void GroupedMusic(string mode) => AlbumShelf(mode);

    void AddFolderTree()
    {
        var tree = new GlassTreeView { Dock = DockStyle.Left, Width = 180, BackColor = UiTheme.Surface, ForeColor = UiTheme.Ink, BorderStyle = BorderStyle.None, HideSelection = false };
        var nodes = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);
        string kind = page == "音乐库" ? "music" : "video";
        TreeNode Ensure(string path, string boundary)
        {
            if (nodes.TryGetValue(path, out var node)) return node;
            string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            node = new TreeNode(name.Length > 0 ? name : path) { Tag = path, ToolTipText = path };
            nodes[path] = node;
            string? parent = Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar));
            if (!path.Equals(boundary, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(parent) && parent != path) Ensure(parent, boundary).Nodes.Add(node); else tree.Nodes.Add(node);
            return node;
        }
        foreach (string folder in store.Media.Where(m => m.Kind == kind).Select(m => m.Folder).Distinct().OrderBy(Scanner.Natural))
        {
            string boundary = store.State.Roots.Select(r => r.Path.TrimEnd(Path.DirectorySeparatorChar)).Where(p => folder.Equals(p, StringComparison.OrdinalIgnoreCase) || folder.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).OrderByDescending(p => p.Length).FirstOrDefault() ?? folder;
            Ensure(folder, boundary);
        }
        if (group.StartsWith("目录：") && nodes.TryGetValue(group[3..], out var current)) { tree.SelectedNode = current; current.EnsureVisible(); }
        else foreach (TreeNode node in tree.Nodes) node.Expand();
        tree.AfterSelect += (_, e) => { if (e.Node?.Tag is string path) { group = "目录：" + path; Render(); } };
        var host = new GlassCanvas { Dock = DockStyle.Fill };
        if (grid != null) host.Controls.Add(grid.Parent is GlassCanvas rowsHost ? rowsHost : grid);
        host.Controls.Add(tree);
        content.Controls.Add(host); host.BringToFront();
        void ArrangeTree()
        {
            if (host.IsDisposed || dpiReady && !dpiScaled.Contains(host)) return;
            float scale = dpiReady ? dpiFactor : 1;
            int preferred = (int)((host.ClientSize.Width / scale < 640 ? 120 : Math.Clamp(host.ClientSize.Width / scale * .25f, 200, 235)) * scale);
            tree.Width = Math.Max((int)(80 * scale), Math.Min(preferred, host.ClientSize.Width - (int)(160 * scale)));
        }
        host.Resize += (_, _) => ArrangeTree();
        if (IsHandleCreated) BeginInvoke((Action)ArrangeTree);
    }
}
