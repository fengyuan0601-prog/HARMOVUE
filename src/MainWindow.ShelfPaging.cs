// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

namespace LocalMedia;

public sealed partial class MainWindow
{
    static readonly SemaphoreSlim shelfProbeSlots = new(2, 2);
    int videoShelfPage, musicShelfPage;
    string videoShelfKey = "", musicShelfKey = "";
    readonly Dictionary<string, (VirtualMediaShelf Shelf, string Source)> libraryShelves = [];
    string ShelfKey(string mode) => string.Join('\u001f', page, mode, group, search.Text.Trim(), selectedList?.Name ?? "", selectedList == null ? "" : string.Join('\u001e', selectedList.Items));
    string ShelfSource(string key, int index) => $"{key}|{index}|{store.Revision}|{searchRevision}|{searchPending}";
    VirtualMediaShelf AcquireShelf(string name, string source, bool video, out bool unchanged)
    {
        if (libraryShelves.TryGetValue(name, out var cached) && !cached.Shelf.IsDisposed)
        {
            unchanged = cached.Source == source;
            if (!unchanged) { foreach (Control child in cached.Shelf.Controls.Cast<Control>().ToArray()) Release(child); cached.Shelf.Footer = null; libraryShelves[name] = (cached.Shelf, source); }
            return cached.Shelf;
        }
        unchanged = false;
        var shelf = new VirtualMediaShelf { Dock = DockStyle.Fill, VideoMode = video, UiScale = DeviceDpi / 96f, Font = OwnedFont(11) };
        libraryShelves[name] = (shelf, source); return shelf;
    }
    void ShowShelf(VirtualMediaShelf shelf)
    {
        cards = shelf; layoutAlbumShelf = shelf.ArrangeShelf;
        if (shelf.Parent != content) content.Controls.Add(shelf);
        shelf.Visible = true; shelf.BringToFront();
    }
    void SetShelfFooter(VirtualMediaShelf shelf, Control? footer)
    {
        if (footer != null && dpiReady && dpiScaled.Contains(shelf)) { footer.Scale(new SizeF(dpiFactor, dpiFactor)); MarkScaled(footer); }
        shelf.Footer = footer;
    }
    bool RetainLibraryShelf(Control control) => libraryShelves.Values.Any(entry => ReferenceEquals(entry.Shelf, control));
    void HideLibraryShelves() { foreach (var entry in libraryShelves.Values) if (!entry.Shelf.IsDisposed) entry.Shelf.Visible = false; }
    void DisposeLibraryShelves() { foreach (var entry in libraryShelves.Values) entry.Shelf.Dispose(); libraryShelves.Clear(); }
    Control? ShelfPager(Control shelf, int count, int current, Action<int> select)
    {
        int pages = (count + 59) / 60;
        if (pages <= 1) return null;
        var host = new GlassFlowPanel { WrapContents = false, AutoSize = false, Margin = Padding.Empty };
        var previous = Button("‹", () => select(current - 1), 46, 36); previous.AccessibleName = "上一页"; previous.Enabled = current > 0;
        var next = Button("›", () => select(current + 1), 46, 36); next.AccessibleName = "下一页"; next.Enabled = current + 1 < pages;
        var index = Caption($"{current + 1} / {pages}", 9, UiTheme.Muted); index.TextAlign = ContentAlignment.MiddleCenter; index.Size = new Size(86, 36);
        host.Controls.AddRange([previous, index, next]); shelf.Controls.Add(host);
        return host;
    }
    void AttachMediaMenu(Media media, params Control[] targets)
    {
        var context = new ContextMenuStrip { BackColor = UiTheme.Surface, ForeColor = UiTheme.Ink };
        context.Opening += (_, _) =>
        {
            foreach (ToolStripItem item in context.Items.Cast<ToolStripItem>().ToArray()) item.Dispose();
            context.Items.Clear();
            using var source = Menu(media);
            while (source.Items.Count > 0) context.Items.Add(source.Items[0]);
            context.BackColor = UiTheme.Surface; context.ForeColor = UiTheme.Ink;
        };
        foreach (Control target in targets) target.ContextMenuStrip = context;
    }
    Task<Bitmap?> LoadShelfCover(VirtualMediaShelf shelf, VirtualShelfItem item, Media media, CancellationToken cancel)
    {
        var source = new CoverSource(media.Id, media.Path, media.Folder, media.Kind, Path.Combine(store.DirectoryPath, "cache"), media.CoverPath);
        string cue = media.Cue; bool missing = media.CueTrackMissing;
        return Task.Run(async () =>
        {
            cancel.ThrowIfCancellationRequested();
            bool online = !missing && File.Exists(source.Path) && (cue.Length == 0 || File.Exists(cue));
            if (!cancel.IsCancellationRequested && !shelf.IsDisposed && shelf.IsHandleCreated)
                try { shelf.BeginInvoke((Action)(() => { if (!shelf.IsDisposed && !cancel.IsCancellationRequested) shelf.UpdateDetail(item.Id, online ? item.Detail : "文件离线"); })); } catch (InvalidOperationException) { }
            return await ReadCover(source, cancel).ConfigureAwait(false);
        }, cancel);
    }
}
