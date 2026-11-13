// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace LocalMedia;

internal sealed record VirtualShelfItem(string Id, string Title, string Detail,
    object? Tag = null, string Glyph = UiTheme.Music, double Progress = 0);

// One HWND for a shelf, regardless of its logical item count. Artwork work is
// limited to the viewport and shares two process-wide slots across old/new
// pages; disposing a page cancels requests without waiting on a slow drive.
internal sealed class VirtualMediaShelf : GlassCanvas
{
    static readonly SemaphoreSlim coverSlots = new(2, 2);
    const int CacheLimit = 64;
    IReadOnlyList<VirtualShelfItem> items = [];
    readonly Dictionary<int, Bitmap?> covers = [];
    readonly LinkedList<int> cacheOrder = [];
    readonly Dictionary<int, LinkedListNode<int>> cacheNodes = [];
    readonly HashSet<int> requested = [];
    readonly Dictionary<string, (string Text, Color? Color)> details = new(StringComparer.Ordinal);
    readonly object resultGate = new();
    readonly HashSet<Bitmap> postedImages = [];
    CancellationTokenSource lifetime = new();
    bool videoMode, arranging, disposed, initialized;
    float uiScale = 1;
    int columns = 1, edge = 200, imageHeight = 200, rowHeight = 290, gap = 28, selected = -1, hot = -1;
    int activeWorkers, generation, contentHeight;
    Control? footer;
    Font? titleFont, detailFont, glyphFont;

    public VirtualMediaShelf()
    {
        AutoScroll = true; TabStop = true;
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "媒体封面";
        SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
        initialized = true;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool VideoMode { get => videoMode; set { if (videoMode == value) return; videoMode = value; ResetFonts(); ArrangeShelf(); } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float UiScale { get => uiScale; set { float next = Math.Clamp(value, .5f, 4); if (Math.Abs(next - uiScale) < .001f) return; uiScale = next; ArrangeShelf(); } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<VirtualShelfItem, CancellationToken, Task<Bitmap?>>? CoverLoader { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Action<VirtualShelfItem>? ItemClick { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Action<VirtualShelfItem>? ItemDoubleClick { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Action<VirtualShelfItem, Keys>? ItemActivate { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Action<VirtualShelfItem, Point>? ItemContextMenu { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Control? Footer
    {
        get => footer;
        set { if (ReferenceEquals(footer, value)) return; if (footer != null && footer.Parent == this) Controls.Remove(footer); footer = value; if (value != null && value.Parent != this) Controls.Add(value); ArrangeShelf(); }
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex { get => selected; set => SelectItem(value, true); }
    public int ContentHeight => contentHeight;
    public int ItemCount => items.Count;
    public int CoverCacheCount => covers.Count;
    public int PendingCoverCount => activeWorkers;
    public int LastPaintedItemCount { get; private set; }
    public IReadOnlyList<int> VisibleItemIndices => VisibleIndices();

    public void SetItems(IReadOnlyList<VirtualShelfItem> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        RestartCoverRequests();
        foreach (var image in covers.Values) image?.Dispose();
        covers.Clear(); cacheOrder.Clear(); cacheNodes.Clear(); requested.Clear(); details.Clear();
        activeWorkers = 0; items = value.ToArray(); selected = -1; hot = -1;
        AutoScrollPosition = Point.Empty;
        ArrangeShelf();
        AccessibilityNotifyClients(AccessibleEvents.Reorder, -1);
    }

    public void UpdateDetail(string id, string text, Color? color = null)
    {
        if (disposed) return;
        details[id] = (text, color);
        foreach (int index in VisibleIndices()) if (items[index].Id == id) Invalidate(GetItemBounds(index));
    }

    int P(float value) => Math.Max(1, (int)Math.Round(value * uiScale));

    public void ArrangeShelf()
    {
        if (arranging || disposed || IsDisposed) return;
        arranging = true;
        try
        {
            int width = Math.Max(P(120), ClientSize.Width - P(videoMode ? 20 : 24));
            gap = P(videoMode ? 24 : 28);
            columns = videoMode ? Math.Clamp((width + gap) / P(224), 1, 5) : Math.Clamp((width + gap) / (P(176) + gap), 1, 6);
            edge = Math.Max(P(60), Math.Min(P(videoMode ? 260 : 200), (width - (columns - 1) * gap) / columns));
            imageHeight = videoMode ? Math.Max(1, edge * 9 / 16) : edge;
            rowHeight = imageHeight + P(videoMode ? 82 : 90);
            int rows = (items.Count + columns - 1) / columns;
            contentHeight = rows == 0 ? P(12) : P(12) + rows * rowHeight - (videoMode ? 0 : P(32));
            int footerHeight = footer == null ? 0 : P(54);
            Size minimum = new(0, contentHeight + footerHeight);
            if (AutoScrollMinSize != minimum) AutoScrollMinSize = minimum;
            if (footer != null)
                footer.SetBounds(AutoScrollPosition.X, AutoScrollPosition.Y + contentHeight, P(220), P(42));
        }
        finally { arranging = false; }
        Invalidate();
        if (IsHandleCreated) PumpCovers();
    }

    public Rectangle GetItemBounds(int index)
    {
        if (index < 0 || index >= items.Count) return Rectangle.Empty;
        return new Rectangle(AutoScrollPosition.X + index % columns * (edge + gap),
            AutoScrollPosition.Y + P(12) + index / columns * rowHeight, edge, imageHeight + P(58));
    }

    int[] VisibleIndices()
    {
        if (disposed || items.Count == 0 || ClientSize.Height <= 0 || ClientSize.Width <= 0) return [];
        int firstRow = Math.Max(0, (-AutoScrollPosition.Y - P(12)) / Math.Max(1, rowHeight));
        int lastRow = Math.Max(firstRow, (-AutoScrollPosition.Y + ClientSize.Height - P(12)) / Math.Max(1, rowHeight));
        int start = Math.Min(items.Count, firstRow * columns), end = Math.Min(items.Count, (lastRow + 1) * columns);
        var visible = new List<int>(Math.Max(0, end - start));
        for (int index = start; index < end; index++) if (GetItemBounds(index).IntersectsWith(ClientRectangle)) visible.Add(index);
        return visible.ToArray();
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); ArrangeShelf(); }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ArrangeShelf(); }
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!initialized || disposed) return;
        if (!Visible)
        {
            // A parked page keeps completed artwork, but must not compete
            // with the foreground shelf for disk/thumbnail worker slots.
            RestartCoverRequests(); hot = -1;
            return;
        }
        // Missing artwork may now be available (for example a reconnected
        // media drive). Retry only the visible window, still two at a time.
        foreach (int index in covers.Where(pair => pair.Value == null).Select(pair => pair.Key).ToArray())
        {
            covers.Remove(index);
            if (cacheNodes.Remove(index, out var node)) cacheOrder.Remove(node);
        }
        Invalidate(); PumpCovers();
    }

    void RestartCoverRequests()
    {
        lock (resultGate)
        {
            generation++;
            foreach (var image in postedImages) image.Dispose();
            postedImages.Clear();
        }
        lifetime.Cancel(); lifetime.Dispose(); lifetime = new();
        requested.Clear(); activeWorkers = 0;
    }

    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); ResetFonts(); }
    void ResetFonts() { titleFont?.Dispose(); detailFont?.Dispose(); glyphFont?.Dispose(); titleFont = detailFont = glyphFont = null; Invalidate(); }
    protected override void OnScroll(ScrollEventArgs e) { base.OnScroll(e); hot = -1; PumpCovers(); AccessibilityNotifyClients(AccessibleEvents.Reorder, -1); }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); hot = -1; PumpCovers(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); if (selected < 0 && items.Count > 0) SelectItem(0, false); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var measurement = UiPerformance.MeasurePaint("paint_virtual_shelf");
        base.OnPaint(e);
        titleFont ??= new Font(Font.FontFamily, videoMode ? 11 : 12, videoMode ? FontStyle.Regular : FontStyle.Bold);
        detailFont ??= new Font(Font.FontFamily, videoMode ? 9 : 10, FontStyle.Regular);
        glyphFont ??= new Font(UiTheme.IconFamily, videoMode ? 25 : 30, FontStyle.Regular);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int drawn = 0;
        foreach (int index in VisibleIndices())
        {
            Rectangle bounds = GetItemBounds(index);
            if (!bounds.IntersectsWith(e.ClipRectangle)) continue;
            PaintItem(e.Graphics, index, bounds); drawn++;
        }
        LastPaintedItemCount = drawn;
        PumpCovers();
    }

    void PaintItem(Graphics graphics, int index, Rectangle bounds)
    {
        VirtualShelfItem item = items[index];
        var cover = new Rectangle(bounds.Left, bounds.Top, edge, imageHeight);
        var state = graphics.Save();
        using (var rounded = UiTheme.Round(cover, 7 * uiScale))
        {
            graphics.SetClip(rounded, CombineMode.Intersect);
            using var background = new LinearGradientBrush(cover, UiTheme.Elevated, UiTheme.Surface, 120f);
            graphics.FillRectangle(background, cover);
            if (covers.TryGetValue(index, out var image) && image != null)
            {
                TouchCache(index);
                float scale = Math.Min(cover.Width / (float)image.Width, cover.Height / (float)image.Height);
                float width = image.Width * scale, height = image.Height * scale;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(image, cover.Left + (cover.Width - width) / 2, cover.Top + (cover.Height - height) / 2, width, height);
            }
            else TextRenderer.DrawText(graphics, item.Glyph, glyphFont, cover, UiTheme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            if (double.IsFinite(item.Progress) && item.Progress > 0)
            {
                int height = P(2);
                using var track = new SolidBrush(UiTheme.Border); using var fill = new SolidBrush(UiTheme.Ink);
                graphics.FillRectangle(track, cover.Left, cover.Bottom - height, cover.Width, height);
                graphics.FillRectangle(fill, cover.Left, cover.Bottom - height, (float)(cover.Width * Math.Clamp(item.Progress, 0, 1)), height);
            }
            graphics.Restore(state);
            if (index == hot || index == selected && Focused)
            {
                using var rim = new Pen(Color.FromArgb(UiTheme.IsLight ? 95 : 65, UiTheme.Ink), Math.Max(1, uiScale));
                graphics.DrawPath(rim, rounded);
            }
        }
        var title = new Rectangle(bounds.Left, cover.Bottom + P(videoMode ? 10 : 11), edge, P(videoMode ? 25 : 24));
        var detail = new Rectangle(bounds.Left, cover.Bottom + P(videoMode ? 36 : 37), edge, P(videoMode ? 22 : 21));
        const TextFormatFlags format = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter;
        TextRenderer.DrawText(graphics, item.Title, titleFont, title, UiTheme.Ink, format);
        var text = details.TryGetValue(item.Id, out var changed) ? changed.Text : item.Detail;
        TextRenderer.DrawText(graphics, text, detailFont, detail, changed.Color ?? UiTheme.Muted, format);
        if (index == selected && Focused) ControlPaint.DrawFocusRectangle(graphics, Rectangle.Union(title, detail), UiTheme.Ink, UiTheme.Surface);
    }

    int HitIndex(Point point)
    {
        if (!ClientRectangle.Contains(point)) return -1;
        int x = point.X - AutoScrollPosition.X, y = point.Y - AutoScrollPosition.Y - P(12);
        if (x < 0 || y < 0) return -1;
        int column = x / Math.Max(1, edge + gap), row = y / Math.Max(1, rowHeight);
        int index = row * columns + column;
        return column < columns && index < items.Count && GetItemBounds(index).Contains(point) ? index : -1;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); int index = HitIndex(e.Location);
        if (index == hot) return;
        int previous = hot; hot = index; Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
        if (previous >= 0) Invalidate(GetItemBounds(previous)); if (hot >= 0) Invalidate(GetItemBounds(hot));
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); int previous = hot; hot = -1; Cursor = Cursors.Default; if (previous >= 0) Invalidate(GetItemBounds(previous)); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button is MouseButtons.Left or MouseButtons.Right) { Focus(); int index = HitIndex(e.Location); if (index >= 0) SelectItem(index, false); } }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e); int index = HitIndex(e.Location); if (index < 0) return;
        if (e.Button == MouseButtons.Right) ItemContextMenu?.Invoke(items[index], e.Location);
        else if (e.Button == MouseButtons.Left) ItemClick?.Invoke(items[index]);
    }
    protected override void OnMouseDoubleClick(MouseEventArgs e) { base.OnMouseDoubleClick(e); int index = HitIndex(e.Location); if (e.Button == MouseButtons.Left && index >= 0) ItemDoubleClick?.Invoke(items[index]); }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown or Keys.Enter or Keys.Space || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        int next = selected < 0 ? 0 : selected;
        switch (e.KeyCode)
        {
            case Keys.Left: next--; break;
            case Keys.Right: next++; break;
            case Keys.Up: next -= columns; break;
            case Keys.Down: next += columns; break;
            case Keys.Home: next = 0; break;
            case Keys.End: next = items.Count - 1; break;
            case Keys.PageUp: next -= Math.Max(1, ClientSize.Height / rowHeight) * columns; break;
            case Keys.PageDown: next += Math.Max(1, ClientSize.Height / rowHeight) * columns; break;
            case Keys.Enter:
            case Keys.Space:
                if (selected >= 0 && selected < items.Count) ActivateItem(selected, e.KeyData);
                e.Handled = e.SuppressKeyPress = true; base.OnKeyDown(e); return;
            case Keys.Apps:
                if (selected >= 0) ItemContextMenu?.Invoke(items[selected], GetItemBounds(selected).Location);
                e.Handled = e.SuppressKeyPress = true; base.OnKeyDown(e); return;
            case Keys.F10 when e.Shift:
                if (selected >= 0) ItemContextMenu?.Invoke(items[selected], GetItemBounds(selected).Location);
                e.Handled = e.SuppressKeyPress = true; base.OnKeyDown(e); return;
            default: base.OnKeyDown(e); return;
        }
        if (items.Count > 0) SelectItem(Math.Clamp(next, 0, items.Count - 1), true);
        e.Handled = e.SuppressKeyPress = true; base.OnKeyDown(e);
    }
    void ActivateItem(int index, Keys keys)
    {
        if (ItemActivate != null) ItemActivate(items[index], keys);
        else if (ItemDoubleClick != null) ItemDoubleClick(items[index]); else ItemClick?.Invoke(items[index]);
    }
    void SelectItem(int index, bool scroll)
    {
        int next = items.Count == 0 ? -1 : Math.Clamp(index, 0, items.Count - 1);
        int previous = selected; selected = next;
        if (scroll && next >= 0)
        {
            Rectangle bounds = GetItemBounds(next); int y = -AutoScrollPosition.Y;
            if (bounds.Top < 0) y += bounds.Top;
            else if (bounds.Bottom > ClientSize.Height) y += bounds.Bottom - ClientSize.Height;
            AutoScrollPosition = new Point(0, Math.Max(0, y));
        }
        if (previous >= 0) Invalidate(GetItemBounds(previous)); if (next >= 0) Invalidate(GetItemBounds(next));
        if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.Focus, -1);
        PumpCovers();
    }

    void TouchCache(int index)
    {
        if (cacheNodes.TryGetValue(index, out var node)) { cacheOrder.Remove(node); cacheOrder.AddLast(node); }
    }
    void PumpCovers()
    {
        var loader = CoverLoader;
        if (disposed || !Visible || !IsHandleCreated || loader == null || activeWorkers >= 2) return;
        foreach (int index in VisibleIndices())
        {
            if (covers.ContainsKey(index) || requested.Contains(index)) continue;
            requested.Add(index); activeWorkers++;
            int revision = generation; var item = items[index]; var token = lifetime.Token;
            _ = Task.Run(async () =>
            {
                Bitmap? image = null; bool entered = false;
                try { await coverSlots.WaitAsync(token).ConfigureAwait(false); entered = true; token.ThrowIfCancellationRequested(); image = await loader(item, token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); }
                catch (OperationCanceledException) { }
                catch (Exception error) { UiPerformance.Record("cover_failure", 0, error.GetType().Name); }
                finally { if (entered) coverSlots.Release(); }
                if (token.IsCancellationRequested || disposed || !IsHandleCreated) { image?.Dispose(); return; }
                Bitmap? result = image;
                lock (resultGate)
                {
                    if (disposed || generation != revision || token.IsCancellationRequested) { result?.Dispose(); return; }
                    if (result != null) postedImages.Add(result);
                }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        lock (resultGate) if (result != null && !postedImages.Remove(result)) return;
                        if (disposed || generation != revision) { result?.Dispose(); return; }
                        requested.Remove(index); activeWorkers--;
                        covers[index] = result; cacheNodes[index] = cacheOrder.AddLast(index);
                        while (covers.Count > CacheLimit && cacheOrder.First is { } oldest)
                        {
                            int old = oldest.Value; cacheOrder.RemoveFirst(); cacheNodes.Remove(old);
                            if (covers.Remove(old, out var previous)) previous?.Dispose();
                        }
                        Invalidate(GetItemBounds(index)); PumpCovers();
                    }));
                }
                catch (InvalidOperationException) { lock (resultGate) if (result != null && postedImages.Remove(result)) result.Dispose(); }
            });
            if (activeWorkers >= 2) break;
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new ShelfAccessibility(this);
    sealed class ShelfAccessibility(VirtualMediaShelf owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.List;
        bool FooterVisible => owner.footer is { IsDisposed: false, Visible: true } && owner.footer.Bounds.IntersectsWith(owner.ClientRectangle);
        public override int GetChildCount() => owner.VisibleIndices().Length + (FooterVisible ? 1 : 0);
        public override AccessibleObject? GetChild(int index) { var visible = owner.VisibleIndices(); return index >= 0 && index < visible.Length ? new ItemAccessibility(owner, this, visible[index]) : index == visible.Length && FooterVisible ? owner.footer!.AccessibilityObject : null; }
        public override AccessibleObject? GetFocused() => owner.Focused && owner.selected >= 0 ? new ItemAccessibility(owner, this, owner.selected) : null;
        public override AccessibleObject? GetSelected() => owner.selected >= 0 ? new ItemAccessibility(owner, this, owner.selected) : null;
        public override AccessibleObject? HitTest(int x, int y) { int index = owner.IsHandleCreated ? owner.HitIndex(owner.PointToClient(new Point(x, y))) : -1; return index >= 0 ? new ItemAccessibility(owner, this, index) : base.HitTest(x, y); }
    }
    sealed class ItemAccessibility(VirtualMediaShelf owner, AccessibleObject parent, int index) : AccessibleObject
    {
        readonly int revision = owner.generation;
        bool Valid => !owner.disposed && owner.generation == revision && index >= 0 && index < owner.items.Count;
        public override string? Name { get => Valid ? owner.items[index].Title : ""; set { } }
        public override string? Description => Valid ? owner.items[index].Detail : "";
        public override AccessibleRole Role => AccessibleRole.ListItem;
        public override AccessibleObject? Parent => parent;
        public override Rectangle Bounds => Valid && owner.IsHandleCreated ? owner.RectangleToScreen(Rectangle.Intersect(owner.GetItemBounds(index), owner.ClientRectangle)) : Rectangle.Empty;
        public override string? DefaultAction => "打开";
        public override AccessibleStates State => !Valid ? AccessibleStates.Unavailable : AccessibleStates.Focusable | AccessibleStates.Selectable | (owner.selected == index ? AccessibleStates.Selected : 0) | (owner.Focused && owner.selected == index ? AccessibleStates.Focused : 0) | (!owner.GetItemBounds(index).IntersectsWith(owner.ClientRectangle) ? AccessibleStates.Offscreen : 0);
        public override void DoDefaultAction() => Dispatch(() => { owner.SelectItem(index, true); owner.ActivateItem(index, Keys.Enter); });
        public override void Select(AccessibleSelection flags) => Dispatch(() => { if ((flags & AccessibleSelection.TakeFocus) != 0) owner.Focus(); owner.SelectItem(index, true); });
        void Dispatch(Action action) { if (!Valid || !owner.IsHandleCreated) return; try { if (owner.InvokeRequired) owner.BeginInvoke(action); else action(); } catch (InvalidOperationException) { } }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            lock (resultGate) { disposed = true; foreach (var image in postedImages) image.Dispose(); postedImages.Clear(); }
            generation++; lifetime.Cancel(); lifetime.Dispose();
            foreach (var image in covers.Values) image?.Dispose(); covers.Clear(); cacheOrder.Clear(); cacheNodes.Clear(); requested.Clear();
            titleFont?.Dispose(); detailFont?.Dispose(); glyphFont?.Dispose(); titleFont = detailFont = glyphFont = null;
            items = []; details.Clear(); selected = hot = -1;
            ItemClick = ItemDoubleClick = null; ItemActivate = null; ItemContextMenu = null; CoverLoader = null;
        }
        base.Dispose(disposing);
    }
}
