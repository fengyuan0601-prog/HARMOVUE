// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace LocalMedia;

enum GlassMaterial { Window, Rail, Card }

// Backdrop coordinates are shared by every glass descendant. A label or button
// therefore repaints the same material underneath it instead of introducing a
// fresh tint rectangle or restarting the gradient at its own top-left corner.
class GlassCanvas : Panel
{
    bool floating;
    GlassMaterial material;
    int cornerRadius = 24;
    Padding surfaceInset;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public GlassMaterial Material
    {
        get => material;
        set { if (material == value) return; material = value; Invalidate(true); }
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CornerRadius
    {
        get => cornerRadius;
        set { cornerRadius = Math.Max(0, value); Invalidate(true); }
    }
    // Logical pixels, intentionally independent from the content Padding.
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Padding SurfaceInset
    {
        get => surfaceInset;
        set { surfaceInset = value; Invalidate(true); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Floating
    {
        get => floating;
        set { if (floating == value) return; floating = value; Invalidate(true); }
    }

    public GlassCanvas()
    {
        BackColor = UiTheme.Background;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
        DoubleBuffered = false;
    }

    protected override void OnPaintBackground(PaintEventArgs e) => PaintSurface(this, e.Graphics, Floating);
    protected override void OnScroll(ScrollEventArgs e) { base.OnScroll(e); Invalidate(true); }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        Point previous = AutoScrollPosition;
        base.OnMouseWheel(e);
        // ScrollableControl's wheel path moves child pixels without OnScroll.
        // The shared backdrop belongs to window coordinates, not those pixels.
        if (!IsDisposed && AutoScrollPosition != previous) Invalidate(true);
    }

    internal static void PaintSurface(Control panel, Graphics graphics, bool floating = false)
    {
        using var measurement = UiPerformance.MeasurePaint("paint_surface");
        if (panel.ClientSize.Width <= 0 || panel.ClientSize.Height <= 0) return;
        var state = graphics.Save();
        try
        {
            graphics.SetClip(panel.ClientRectangle, CombineMode.Intersect);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            if (!IsGlassColor(panel.BackColor))
            {
                using var solid = new SolidBrush(Opaque(panel.BackColor));
                graphics.FillRectangle(solid, panel.ClientRectangle);
                return;
            }
            if (MaterialSurfaceCache.TryPaint(panel, graphics)) return;
            PaintStaticMaterial(panel, graphics, useBackdropCache: true);
        }
        finally { graphics.Restore(state); }
    }

    // Used by the optional material cache as well as the direct path. This only
    // draws our tint and material geometry; it never paints a child or captures
    // desktop pixels, text, album artwork or a video surface.
    internal static void PaintStaticMaterial(Control panel, Graphics graphics, bool useBackdropCache)
    {
        graphics.CompositingMode = CompositingMode.SourceCopy;
        PaintBackdrop(panel, graphics, useBackdropCache);
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var measurement = UiPerformance.MeasurePaint("paint_material_layers");
        // Include material-bearing ancestors in their original back-to-front
        // order. Layout descendants must inherit exactly the same material.
        var layers = new List<(GlassCanvas Canvas, Point Offset)>();
        Point offset = Point.Empty;
        for (Control? ancestor = panel; ancestor != null; ancestor = ancestor.Parent)
        {
            if (ancestor is GlassCanvas canvas && (canvas.Material != GlassMaterial.Window || canvas.Floating))
                layers.Add((canvas, offset));
            if (ancestor.Parent == null) break;
            offset.Offset(-ancestor.Left, -ancestor.Top);
        }
        for (int index = layers.Count - 1; index >= 0; index--) PaintLayer(graphics, layers[index].Canvas, layers[index].Offset);
    }

    internal static void BackdropParameters(Control panel, out Control root, out Point offset, out Color top, out Color bottom)
    {
        root = panel;
        offset = Point.Empty;
        while (root.Parent != null) { offset.Offset(root.Left, root.Top); root = root.Parent; }
        bool glass = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);
        // Shared opacity and geometry across themes; only the neutral substrate
        // changes. The light surface must not become a separate opaque white UI.
        top = UiTheme.IsLight ? Color.FromArgb(glass ? 224 : 255, 220, 224, 230) : Color.FromArgb(glass ? 224 : 255, 23, 26, 30);
        bottom = UiTheme.IsLight ? Color.FromArgb(glass ? 236 : 255, 210, 216, 224) : Color.FromArgb(glass ? 236 : 255, 15, 18, 22);
    }

    static void PaintBackdrop(Control panel, Graphics graphics, bool useCache)
    {
        BackdropParameters(panel, out var root, out var offset, out var top, out var bottom);
        var bounds = new RectangleF(-offset.X, -offset.Y, Math.Max(1, root.ClientSize.Width), Math.Max(1, root.ClientSize.Height));
        if (SystemInformation.HighContrast)
        {
            GlassBackdropCache.Invalidate(root);
            using var solid = new SolidBrush(SystemColors.Window); graphics.FillRectangle(solid, panel.ClientRectangle); return;
        }
        if (useCache && GlassBackdropCache.TryPaint(root, graphics, offset, top, bottom)) return;
        using (UiPerformance.MeasurePaint("paint_backdrop_uncached"))
            GlassBackdropCache.FillTint(graphics, bounds, panel.ClientRectangle, top, bottom);
    }

    static void PaintLayer(Graphics graphics, GlassCanvas canvas, Point offset)
    {
        float s = canvas.DeviceDpi / 96f;
        Padding inset = canvas.Floating ? new Padding(18, 8, 18, 10) : canvas.SurfaceInset;
        // Reserve the same small halo gutter in both themes so the soft edge
        // separation is not clipped at the native control boundary.
        var bounds = new RectangleF(offset.X + inset.Left * s + 2 * s, offset.Y + inset.Top * s + 2 * s,
            canvas.ClientSize.Width - inset.Horizontal * s - 4 * s, canvas.ClientSize.Height - inset.Vertical * s - 4 * s);
        if (bounds.Width <= 1 || bounds.Height <= 1) return;
        float radius = (canvas.Floating ? 25 : canvas.CornerRadius) * s;
        UiTheme.GlassPlate(graphics, bounds, radius, canvas.Material == GlassMaterial.Card || canvas.Floating, s);
    }

    // Paint the same parent coordinates into an owner-drawn child. The inherited
    // clip and transform remain intact, including clipped/scrolling descendants.
    public static bool PaintParentSlice(Control child, Graphics graphics)
    {
        using var measurement = UiPerformance.MeasurePaint("paint_parent_slice");
        int x = child.Left, y = child.Top;
        for (Control? parent = child.Parent; parent != null; parent = parent.Parent)
        {
            if (parent is GlassCanvas || parent is GlassFlowPanel)
            {
                var state = graphics.Save();
                try
                {
                    graphics.SetClip(child.ClientRectangle, CombineMode.Intersect);
                    graphics.TranslateTransform(-x, -y);
                    PaintSurface(parent, graphics, parent is GlassCanvas canvas && canvas.Floating);
                }
                finally { graphics.Restore(state); }
                return true;
            }
            // An ordinary opaque surface is a real boundary (for example video
            // controls or a text field), not another inherited glass layout host.
            if (parent.BackColor.A == 255 && !IsGlassColor(parent.BackColor)) return false;
            x += parent.Left; y += parent.Top;
        }
        return false;
    }

    public static void PaintParent(Control child, Graphics graphics)
    {
        if (PaintParentSlice(child, graphics)) return;
        Color background = child.Parent?.BackColor ?? UiTheme.Background;
        var state = graphics.Save();
        try
        {
            graphics.SetClip(child.ClientRectangle, CombineMode.Intersect);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            using var solid = new SolidBrush(Opaque(background));
            graphics.FillRectangle(solid, child.ClientRectangle);
        }
        finally { graphics.Restore(state); }
    }

    static bool IsGlassColor(Color color) => color.R == 0 && color.G == 0 && color.B == 0;
    static Color Opaque(Color color) => Color.FromArgb(255, color.R, color.G, color.B);
}

sealed class GlassFlowPanel : FlowLayoutPanel
{
    public GlassFlowPanel()
    {
        BackColor = UiTheme.Background;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
        DoubleBuffered = false;
    }

    protected override void OnPaintBackground(PaintEventArgs e) => GlassCanvas.PaintSurface(this, e.Graphics);
    protected override void OnScroll(ScrollEventArgs e) { base.OnScroll(e); Invalidate(true); }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        Point previous = AutoScrollPosition;
        base.OnMouseWheel(e);
        if (!IsDisposed && AutoScrollPosition != previous) Invalidate(true);
    }
}

sealed class AlbumTilePanel : GlassCanvas
{
    public AlbumTilePanel()
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) Focus();
        base.OnMouseDown(e);
    }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space && !e.Handled)
        {
            e.Handled = true; e.SuppressKeyPress = true; OnClick(EventArgs.Empty);
        }
    }
}

sealed class GlassLabel : Label
{
    public GlassLabel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }
    protected override void OnPaintBackground(PaintEventArgs e) => GlassCanvas.PaintParent(this, e.Graphics);
    protected override void OnPaint(PaintEventArgs e)
    {
        using var measurement = UiPerformance.MeasurePaint("paint_label");
        var alignment = TextAlign switch
        {
            ContentAlignment.TopCenter or ContentAlignment.MiddleCenter or ContentAlignment.BottomCenter => StringAlignment.Center,
            ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight => StringAlignment.Far,
            _ => StringAlignment.Near
        };
        var vertical = TextAlign switch
        {
            ContentAlignment.MiddleLeft or ContentAlignment.MiddleCenter or ContentAlignment.MiddleRight => StringAlignment.Center,
            ContentAlignment.BottomLeft or ContentAlignment.BottomCenter or ContentAlignment.BottomRight => StringAlignment.Far,
            _ => StringAlignment.Near
        };
        using var format = new StringFormat { Alignment = alignment, LineAlignment = vertical, Trimming = AutoEllipsis ? StringTrimming.EllipsisCharacter : StringTrimming.None };
        if (AutoEllipsis) format.FormatFlags |= StringFormatFlags.NoWrap;
        using var ink = new SolidBrush(ForeColor);
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        e.Graphics.DrawString(Text, Font, ink, new RectangleF(Padding.Left, Padding.Top, Math.Max(1, Width - Padding.Horizontal), Math.Max(1, Height - Padding.Vertical)), format);
    }
}

// The native edit control draws with GDI. Give only this child HWND an opaque
// redirection surface so text selection and the caret stay native over Acrylic.
sealed class GlassTextBox : TextBox
{
    bool layered = OperatingSystem.IsWindowsVersionAtLeast(6, 2);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    static extern bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);
    protected override CreateParams CreateParams
    {
        get { var parameters = base.CreateParams; if (layered) parameters.ExStyle |= 0x00080000; return parameters; }
    }
    protected override void CreateHandle()
    {
        try { base.CreateHandle(); }
        catch (Win32Exception) when (layered) { layered = false; base.CreateHandle(); }
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (layered && !SetLayeredWindowAttributes(Handle, 0, 255, 2)) { layered = false; RecreateHandle(); }
    }
}

sealed class GlassDataGridView : DataGridView
{
    protected override void WndProc(ref Message m)
    {
        // Do not republish providers re-entrantly while the process disconnects
        // them. Ordinary running/tray windows retain the full native UIA tree.
        if (UiAccessibility.Exiting && m.Msg == 0x003D) { m.Result = 0; return; }
        base.WndProc(ref m);
    }
    int[]? accessibleVisibleRows;
    int[]? accessibleViewportRows;
    bool accessibleViewportNotificationPending, accessibleProviderCreated;
    public GlassDataGridView() => RowTemplate = new GlassGridRow();

    protected override AccessibleObject CreateAccessibilityInstance()
    {
        accessibleProviderCreated = true;
        return new ViewportAccessibility(this);
    }

    // The native grid exposes every virtual row to UIA sibling traversal.
    // Reading Rows[index] unshares that row and all its cells, so a background
    // accessibility client could turn 10,000 virtual rows into 60,000 objects
    // on the UI thread. Enumerate the viewport like a virtualized list. Native
    // GridPattern.GetItem/RowCount, selection and keyboard scrolling remain
    // available for direct access to any item in the complete library.
    internal int[] AccessibleViewportRows
    {
        get
        {
            if (accessibleViewportRows != null) return accessibleViewportRows;
            var indices = new List<int>();
            if (IsHandleCreated)
                for (int index = Rows.GetFirstRow(DataGridViewElementStates.Displayed); index >= 0;
                    index = Rows.GetNextRow(index, DataGridViewElementStates.Displayed)) indices.Add(index);
            return accessibleViewportRows = indices.ToArray();
        }
    }

    void InvalidateAccessibleViewport()
    {
        accessibleViewportRows = null;
        if (!accessibleProviderCreated || accessibleViewportNotificationPending || !IsHandleCreated || IsDisposed || UiAccessibility.Exiting) return;
        accessibleViewportNotificationPending = true;
        try
        {
            BeginInvoke(() =>
            {
                accessibleViewportNotificationPending = false;
                if (!IsDisposed && IsHandleCreated && !UiAccessibility.Exiting)
                    AccessibilityNotifyClients(AccessibleEvents.Reorder, -1);
            });
        }
        catch (InvalidOperationException) { accessibleViewportNotificationPending = false; }
    }

    sealed class ViewportAccessibility(GlassDataGridView grid) : DataGridViewAccessibleObject(grid)
    {
        public override int GetChildCount()
        {
            if (!grid.IsHandleCreated) return base.GetChildCount();
            if (grid.Columns.Count == 0) return 0;
            return base.GetChildCount() - grid.Rows.GetRowCount(DataGridViewElementStates.Visible) + grid.AccessibleViewportRows.Length;
        }

        public override AccessibleObject? GetChild(int index)
        {
            if (!grid.IsHandleCreated) return base.GetChild(index);
            if (index < 0 || grid.Columns.Count == 0) return null;
            int header = grid.ColumnHeadersVisible ? 1 : 0;
            if (header == 1 && index == 0) return base.GetChild(0);
            int ordinal = index - header;
            int[] rows = grid.AccessibleViewportRows;
            if (ordinal < rows.Length) return grid.Rows[rows[ordinal]].AccessibilityObject;
            // Keep the native horizontal/vertical scrollbar providers after
            // the realized rows, including their range and scroll patterns.
            return base.GetChild(index - rows.Length + grid.Rows.GetRowCount(DataGridViewElementStates.Visible));
        }
    }

    // .NET 10's accessible cell/row Name calls GetVisibleIndex, whose nested
    // traversal is quadratic for one late row (dotnet/winforms#15171). Keep
    // the native providers; only cache the visible ordinal needed for labels.
    internal int AccessibleVisibleIndex(DataGridViewRow row)
    {
        int index = row.Index;
        if (index < 0)
        {
            // Shared rows have no unique Index. UIA normally unshares a row
            // when obtaining it; preserve first-match semantics for callers
            // that explicitly request a SharedRow's accessibility object.
            for (int i = 0; i < Rows.Count; i++)
                if (ReferenceEquals(Rows.SharedRow(i), row)) { index = i; break; }
        }
        if (index < 0 || index >= Rows.Count) return -1;
        if (accessibleVisibleRows == null || accessibleVisibleRows.Length != Rows.Count)
        {
            var ordinals = new int[Rows.Count];
            int visible = 0;
            for (int i = 0; i < ordinals.Length; i++)
                ordinals[i] = (Rows.GetRowState(i) & DataGridViewElementStates.Visible) != 0 ? visible++ : -1;
            accessibleVisibleRows = ordinals;
        }
        return accessibleVisibleRows[index];
    }
    protected override void OnRowsAdded(DataGridViewRowsAddedEventArgs e) { accessibleVisibleRows = null; InvalidateAccessibleViewport(); base.OnRowsAdded(e); }
    protected override void OnRowsRemoved(DataGridViewRowsRemovedEventArgs e) { accessibleVisibleRows = null; InvalidateAccessibleViewport(); base.OnRowsRemoved(e); }
    protected override void OnRowStateChanged(int rowIndex, DataGridViewRowStateChangedEventArgs e)
    {
        if ((e.StateChanged & DataGridViewElementStates.Visible) != 0) accessibleVisibleRows = null;
        if ((e.StateChanged & (DataGridViewElementStates.Visible | DataGridViewElementStates.Displayed | DataGridViewElementStates.Frozen)) != 0) InvalidateAccessibleViewport();
        base.OnRowStateChanged(rowIndex, e);
    }
    protected override void OnSorted(EventArgs e) { accessibleVisibleRows = null; InvalidateAccessibleViewport(); base.OnSorted(e); }
    protected override void OnScroll(ScrollEventArgs e) { base.OnScroll(e); InvalidateAccessibleViewport(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); InvalidateAccessibleViewport(); }
    protected override void OnHandleDestroyed(EventArgs e) { accessibleViewportRows = null; accessibleViewportNotificationPending = false; base.OnHandleDestroyed(e); }

    bool layered = OperatingSystem.IsWindowsVersionAtLeast(6, 2);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    static extern bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);
    protected override CreateParams CreateParams
    {
        get { var parameters = base.CreateParams; if (layered) parameters.ExStyle |= 0x00080000; return parameters; }
    }
    protected override void CreateHandle()
    {
        try { base.CreateHandle(); }
        catch (Win32Exception) when (layered) { layered = false; base.CreateHandle(); }
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (layered && !SetLayeredWindowAttributes(Handle, 0, 255, 2)) { layered = false; RecreateHandle(); }
    }
}

sealed class GlassGridTextColumn : DataGridViewTextBoxColumn
{
    public GlassGridTextColumn() => CellTemplate = new GlassGridTextCell();
}

sealed class GlassGridTextCell : DataGridViewTextBoxCell
{
    public GlassGridTextCell() { }
    protected override AccessibleObject CreateAccessibilityInstance() => new CellAccessibility(this);
    sealed class CellAccessibility(GlassGridTextCell cell) : DataGridViewTextBoxCellAccessibleObject(cell)
    {
        public override string? Name
        {
            get
            {
                if (cell.DataGridView is not GlassDataGridView grid || cell.OwningRow == null || cell.OwningColumn == null) return base.Name;
                string name = $"{cell.OwningColumn.HeaderText}，第 {grid.AccessibleVisibleIndex(cell.OwningRow) + 1} 行";
                if (cell.OwningColumn.SortMode != DataGridViewColumnSortMode.NotSortable)
                    name += cell.OwningColumn == grid.SortedColumn ? grid.SortOrder == SortOrder.Ascending ? "，升序" : "，降序" : "，未排序";
                return name;
            }
        }
    }
}

sealed class GlassGridRow : DataGridViewRow
{
    public GlassGridRow() { }
    protected override AccessibleObject CreateAccessibilityInstance() => new RowAccessibility(this);
    sealed class RowAccessibility(GlassGridRow row) : DataGridViewRowAccessibleObject(row)
    {
        public override string Name => row.DataGridView is GlassDataGridView grid
            ? $"第 {(row.Visible ? grid.AccessibleVisibleIndex(row) + 1 : -1)} 行" : base.Name;

        // Native UIA FragmentNavigate delegates to this virtual method. Use
        // real row indices so sibling navigation also avoids GetVisibleIndex.
        public override AccessibleObject? Navigate(AccessibleNavigation direction)
        {
            if (row.DataGridView is not GlassDataGridView grid || row.Index < 0) return base.Navigate(direction);
            if (direction is AccessibleNavigation.Next or AccessibleNavigation.Down)
            {
                int next = grid.Rows.GetNextRow(row.Index, grid.IsHandleCreated ? DataGridViewElementStates.Displayed : DataGridViewElementStates.Visible);
                return next >= 0 ? grid.Rows[next].AccessibilityObject : null;
            }
            if (direction is AccessibleNavigation.Previous or AccessibleNavigation.Up)
            {
                int previous = grid.Rows.GetPreviousRow(row.Index, grid.IsHandleCreated ? DataGridViewElementStates.Displayed : DataGridViewElementStates.Visible);
                return previous >= 0 ? grid.Rows[previous].AccessibilityObject
                    : grid.ColumnHeadersVisible ? grid.AccessibilityObject.GetChild(0) : null;
            }
            return base.Navigate(direction);
        }
    }
}

// Like the native edit/grid controls, TreeView uses GDI without alpha. Give it
// an opaque child surface so Acrylic cannot interpret its background as glass.
sealed class GlassTreeView : TreeView
{
    bool layered = OperatingSystem.IsWindowsVersionAtLeast(6, 2);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    static extern bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);
    protected override CreateParams CreateParams
    {
        get { var parameters = base.CreateParams; if (layered) parameters.ExStyle |= 0x00080000; return parameters; }
    }
    protected override void CreateHandle()
    {
        try { base.CreateHandle(); }
        catch (Win32Exception) when (layered) { layered = false; base.CreateHandle(); }
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (layered && !SetLayeredWindowAttributes(Handle, 0, 255, 2)) { layered = false; RecreateHandle(); }
    }
}
