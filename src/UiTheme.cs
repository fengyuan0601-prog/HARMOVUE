// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;

namespace LocalMedia;

// Shared native UI primitives. No additional rendering runtime or playback surface.
static class UiTheme
{
    // Black is a rendering sentinel for the DWM-backed canvas, not the visible
    // theme background. It also remains the actual opaque video-surface color.
    public static readonly Color Background = Color.Black;
    public static readonly Color Sidebar = Color.Black;
    sealed record Palette(Color Surface, Color Elevated, Color Border, Color Ink, Color Muted, Color Accent, Color AccentSoft, Color Caption, Color Warning);
    static readonly Palette Dark = new(Color.FromArgb(23, 26, 30), Color.FromArgb(34, 38, 43), Color.FromArgb(57, 63, 70), Color.FromArgb(243, 245, 247), Color.FromArgb(164, 171, 179), Color.FromArgb(235, 240, 244), Color.FromArgb(49, 56, 64), Color.FromArgb(20, 22, 25), Color.FromArgb(219, 163, 130));
    static readonly Palette Light = new(Color.FromArgb(248, 249, 250), Color.FromArgb(239, 242, 245), Color.FromArgb(207, 212, 218), Color.FromArgb(31, 35, 40), Color.FromArgb(95, 103, 112), Color.FromArgb(44, 51, 59), Color.FromArgb(220, 225, 231), Color.FromArgb(243, 245, 247), Color.FromArgb(143, 86, 48));
    static Palette Current => IsLight ? Light : Dark;
    public static bool IsLight { get; private set; }
    public static void SetLight(bool value)
    {
        IsLight = value;
        // .NET 10 updates application system colors in place. Existing native
        // scrollbars also retain a per-HWND theme; refresh those below separately.
        Application.SetColorMode(!value && !SystemInformation.HighContrast ? SystemColorMode.Dark : SystemColorMode.Classic);
    }
    public static Color Surface => Current.Surface;
    public static Color Elevated => Current.Elevated;
    public static Color Border => Current.Border;
    public static Color Ink => Current.Ink;
    public static Color Muted => Current.Muted;
    public static Color Accent => Current.Accent;
    public static Color AccentSoft => Current.AccentSoft;
    public static Color CaptionBackground => Current.Caption;
    public static Color Warning => Current.Warning;
    public static Color PrimaryInk => IsLight ? Color.FromArgb(250, 251, 252) : Color.FromArgb(24, 28, 33);

    // Owner-drawn surfaces read the palette at paint time; native HWND controls
    // retain their assigned colors. Update only former palette colors, keeping
    // transparent hosts, black video and media artwork unchanged.
    public static void RefreshControlColors(Control root, bool previousLight)
    {
        if (root.IsDisposed) return;
        Palette old = previousLight ? Light : Dark;
        Color[] before = [old.Surface, old.Elevated, old.Border, old.Ink, old.Muted, old.Accent, old.AccentSoft, old.Caption, old.Warning];
        Color[] after = [Surface, Elevated, Border, Ink, Muted, Accent, AccentSoft, CaptionBackground, Warning];
        Color Map(Color color)
        {
            if (color.IsEmpty || color.A != 255 || color.ToArgb() == Color.Black.ToArgb()) return color;
            for (int index = 0; index < before.Length; index++) if (color.ToArgb() == before[index].ToArgb()) return after[index];
            return color;
        }
        // Capture the entire tree before setting any color. A child's effective
        // ForeColor/BackColor can inherit from its parent; reading it after the
        // parent changes would map the new palette a second time. In particular,
        // Light.Caption and Dark.Ink intentionally share the same RGB value.
        var updates = new List<Action>();
        var controls = new HashSet<Control>(ReferenceEqualityComparer.Instance);
        var items = new HashSet<ToolStripItem>(ReferenceEqualityComparer.Instance);
        var styles = new HashSet<DataGridViewCellStyle>(ReferenceEqualityComparer.Instance);
        void CaptureCellStyle(DataGridViewCellStyle style)
        {
            if (!styles.Add(style)) return;
            Color background = Map(style.BackColor), foreground = Map(style.ForeColor);
            Color selectedBackground = Map(style.SelectionBackColor), selectedForeground = Map(style.SelectionForeColor);
            updates.Add(() =>
            {
                style.BackColor = background; style.ForeColor = foreground;
                style.SelectionBackColor = selectedBackground; style.SelectionForeColor = selectedForeground;
            });
        }
        void CaptureMenuItems(ToolStripItemCollection collection)
        {
            foreach (ToolStripItem item in collection)
            {
                if (!items.Add(item)) continue;
                Color background = Map(item.BackColor), foreground = Map(item.ForeColor);
                updates.Add(() => { if (!item.IsDisposed) { item.BackColor = background; item.ForeColor = foreground; } });
                if (item is ToolStripDropDownItem dropDown) CaptureMenuItems(dropDown.DropDownItems);
            }
        }
        void CaptureControl(Control control)
        {
            if (control.IsDisposed || !controls.Add(control)) return;
            Color background = Map(control.BackColor), foreground = Map(control.ForeColor);
            updates.Add(() =>
            {
                if (control.IsDisposed) return;
                control.BackColor = background; control.ForeColor = foreground; control.Invalidate();
            });
            if (previousLight != IsLight && (control is ScrollableControl { AutoScroll: true } or ListBox or ScrollBar
                || control is TextBox { Multiline: true } textBox && textBox.ScrollBars != ScrollBars.None))
                updates.Add(() => RefreshNativeScrollTheme(control));
            if (control is DataGridView grid)
            {
                Color gridBackground = Map(grid.BackgroundColor), gridLines = Map(grid.GridColor);
                updates.Add(() => { if (!grid.IsDisposed) { grid.BackgroundColor = gridBackground; grid.GridColor = gridLines; } });
                CaptureCellStyle(grid.DefaultCellStyle); CaptureCellStyle(grid.RowsDefaultCellStyle); CaptureCellStyle(grid.AlternatingRowsDefaultCellStyle);
                CaptureCellStyle(grid.ColumnHeadersDefaultCellStyle); CaptureCellStyle(grid.RowHeadersDefaultCellStyle);
                foreach (DataGridViewColumn column in grid.Columns) CaptureCellStyle(column.DefaultCellStyle);
            }
            if (control is ToolStrip strip) CaptureMenuItems(strip.Items);
            if (control.ContextMenuStrip is { } menu) CaptureControl(menu);
            foreach (Control child in control.Controls) CaptureControl(child);
        }
        CaptureControl(root);
        foreach (Action update in updates) update();
    }
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(nint window, string? subAppName, string? subIdList);
    static void RefreshNativeScrollTheme(Control control)
    {
        // Never create/recreate a handle for a theme change, and never touch the
        // playback surface. NULL removes .NET 10's retained DarkMode_Explorer
        // association; the API sends WM_THEMECHANGED to this existing HWND.
        if (control.IsDisposed || !control.IsHandleCreated) return;
        bool dark = !IsLight && !SystemInformation.HighContrast && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
        SetWindowTheme(control.Handle, dark ? "DarkMode_Explorer" : null, null);
        control.Invalidate();
    }
    public static string IconFamily => IconFont.Family;
    static class IconFont
    {
        internal static readonly string Family = FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons")
            ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    }
    public const string Home = "\uE80F", Music = "\uE8D6", Video = "\uE714", Queue = "\uE8FD", Library = "\uE8F1", Favorite = "\uE734", Settings = "\uE713";
    public const string Play = "\uE768", Pause = "\uE769", Previous = "\uE892", Next = "\uE893", Search = "\uE721", Add = "\uE710", Open = "\uE8E5", Folder = "\uE8D5", More = "\uE712", Volume = "\uE767", Fullscreen = "\uE740";
    public static GraphicsPath Round(RectangleF rectangle, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
        var path = new GraphicsPath();
        if (d <= 0) { path.AddRectangle(rectangle); return path; }
        path.AddArc(rectangle.X, rectangle.Y, d, d, 180, 90);
        path.AddArc(rectangle.Right - d, rectangle.Y, d, d, 270, 90);
        path.AddArc(rectangle.Right - d, rectangle.Bottom - d, d, d, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - d, d, d, 90, 90);
        path.CloseFigure(); return path;
    }
    public static void Glyph(Graphics graphics, string glyph, Rectangle bounds, Color color, float points = 13)
    {
        using var font = new Font(IconFamily, points, FontStyle.Regular, GraphicsUnit.Point);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.DrawString(glyph, font, brush, bounds, format);
    }
    // This is a material surface, not decorative artwork: a translucent body
    // and a quiet rim. Children repaint this exact
    // surface through GlassCanvas so no rectangular control patches appear.
    public static void GlassPlate(Graphics graphics, RectangleF bounds, float radius, bool prominent = false, float deviceScale = 1f, float opacity = 1f)
    {
        if (bounds.Width <= 1 || bounds.Height <= 1) return;
        using var path = Round(bounds, radius);
        if (SystemInformation.HighContrast)
        {
            using var solid = new SolidBrush(SystemColors.Window);
            using var outline = new Pen(SystemColors.WindowText);
            graphics.FillPath(solid, path); graphics.DrawPath(outline, path); return;
        }
        // Keep one material geometry and light direction across themes. Light
        // glass needs more white transmission against its brighter substrate;
        // equal alpha values made its body and rim visually disappear.
        float scale = Math.Clamp(deviceScale, 1f, 4f);
        int Alpha(int alpha) => (int)(alpha * Math.Clamp(opacity, 0, 1));
        // Draw only outside the body. A centred stroke beneath translucent
        // fill otherwise survives as an etched inner line next to the gloss.
        var shadowState = graphics.Save();
        try
        {
            using var interior = new Region(path);
            graphics.ExcludeClip(interior);
            using var shadow = new Pen(Color.FromArgb(Alpha(IsLight ? (prominent ? 16 : 10) : 20), Color.Black), 3f * scale);
            graphics.DrawPath(shadow, path);
        }
        finally { graphics.Restore(shadowState); }
        using (var fill = new LinearGradientBrush(bounds,
            Color.FromArgb(Alpha(IsLight ? (prominent ? 82 : 44) : (prominent ? 28 : 16)), 248, 250, 252),
            Color.FromArgb(Alpha(IsLight ? (prominent ? 28 : 12) : (prominent ? 10 : 4)), 228, 233, 239), LinearGradientMode.Vertical))
            graphics.FillPath(fill, path);
        GlassRim(graphics, bounds, radius, prominent, deviceScale, opacity);
    }
    public static void GlassRim(Graphics graphics, RectangleF bounds, float radius, bool prominent = false, float deviceScale = 1f, float opacity = 1f)
    {
        if (bounds.Width <= 1 || bounds.Height <= 1 || SystemInformation.HighContrast) return;
        using var path = Round(bounds, radius);
        float scale = Math.Clamp(deviceScale, 1f, 4f);
        // One continuous reflection on the same path in both themes. A second
        // dark outline made light glass look etched rather than transparent.
        using var reflection = new LinearGradientBrush(bounds, Color.White, Color.White, 125f);
        int A(int value) => (int)((prominent ? value : value * .7f) * Math.Clamp(opacity, 0, 1));
        reflection.InterpolationColors = new ColorBlend
        {
            Positions = [0f, .20f, .48f, .76f, 1f],
            Colors = [Color.FromArgb(A(IsLight ? 240 : 210), 255, 255, 255), Color.FromArgb(A(IsLight ? 205 : 115), 250, 253, 255),
                Color.FromArgb(A(IsLight ? 145 : 62), 238, 245, 250), Color.FromArgb(A(IsLight ? 205 : 130), 250, 253, 255), Color.FromArgb(A(IsLight ? 235 : 190), 255, 255, 255)]
        };
        using var rim = new Pen(reflection, 1.1f * scale);
        graphics.DrawPath(rim, path);
    }
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    [StructLayout(LayoutKind.Sequential)] struct FrameMargins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(nint window, ref FrameMargins margins);
    public static void WindowChrome(Form form, bool glass = true)
    {
        // Applies only to this application's title bar, never to system preferences.
        try
        {
            bool highContrast = SystemInformation.HighContrast;
            int dark = highContrast || IsLight ? 0 : 1; DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int));
            bool enabled = glass && !highContrast && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);
            int backdrop = enabled ? 3 : 1; int backdropResult = DwmSetWindowAttribute(form.Handle, 38, ref backdrop, sizeof(int));
            // A default caption color inherits the Windows accent. Keep the native
            // caption and its hit testing, but give it our neutral surface color.
            int color = ColorTranslator.ToWin32(highContrast ? SystemColors.ActiveCaption : CaptionBackground);
            int captionResult = DwmSetWindowAttribute(form.Handle, 35, ref color, sizeof(int));
            int textColor = ColorTranslator.ToWin32(highContrast ? SystemColors.ActiveCaptionText : Ink);
            int textResult = DwmSetWindowAttribute(form.Handle, 36, ref textColor, sizeof(int));
            int borderColor = ColorTranslator.ToWin32(highContrast ? SystemColors.ActiveBorder : Border);
            int borderResult = DwmSetWindowAttribute(form.Handle, 34, ref borderColor, sizeof(int));
            var margins = enabled ? new FrameMargins { Left = -1, Right = -1, Top = -1, Bottom = -1 } : new FrameMargins();
            int frameResult = DwmExtendFrameIntoClientArea(form.Handle, ref margins);
            if (Environment.GetEnvironmentVariable("LOCALMEDIA_UI_TRACE") == "1" && Environment.GetEnvironmentVariable("LOCALMEDIA_DATA") is string data)
                File.AppendAllText(Path.Combine(data, "backdrop.log"), $"{DateTime.UtcNow:o} Acrylic={enabled} backdrop={backdropResult:X8} frame={frameResult:X8} caption={captionResult:X8} text={textResult:X8} border={borderResult:X8}{Environment.NewLine}");
        }
        catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
    }
}

enum UiButtonStyle { Secondary, Quiet, Primary, Navigation, Tab, Circle }

sealed class UiButton : Button
{
    bool hot, selected, pressed;
    double selectionAmount;
    UiMotion? selectionMotion;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Glyph { get; set; } = "";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public UiButtonStyle Style { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IconOnly { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => selected;
        set
        {
            if (selected == value) return;
            selected = value;
            if (Style is UiButtonStyle.Navigation or UiButtonStyle.Tab)
            {
                selectionMotion ??= new UiMotion(this, "navigation_motion");
                double from = selectionAmount, target = value ? 1 : 0;
                selectionMotion.Run(140, progress => { selectionAmount = from + (target - from) * progress; Invalidate(); });
            }
            else { selectionAmount = value ? 1 : 0; Invalidate(); }
        }
    }
    protected override void Dispose(bool disposing) { if (disposing) selectionMotion?.Dispose(); base.Dispose(disposing); }
    public UiButton()
    {
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        // The shared WinForms BufferedGraphics surface does not preserve the
        // per-HWND alpha required by the extended DWM frame. Draw the complete
        // face directly, like GlassCanvas, rather than reusing an opaque buffer.
        SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
        DoubleBuffered = false;
        BackColor = UiTheme.Surface; ForeColor = UiTheme.Ink; Cursor = Cursors.Hand; UseVisualStyleBackColor = false;
    }
    protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hot = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    // Button's native flat-background pass paints edge fragments before our
    // owner-drawn face. Replace that pass as well as OnPaint.
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e)
    {
        using var measurement = UiPerformance.MeasurePaint("paint_button");
        var g = e.Graphics;
        // Button invalidations can invoke OnPaint without another background
        // pass. Always replace this control's pixels before drawing its face.
        GlassCanvas.PaintParent(this, g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        g.CompositingMode = CompositingMode.SourceOver;
        var bounds = new RectangleF(1.5f * s, 1.5f * s, Math.Max(1, Width - 3 * s), Math.Max(1, Height - 3 * s));
        float radius = Style switch { UiButtonStyle.Navigation => 19 * s, UiButtonStyle.Quiet => 12 * s, _ => Height / 2f };
        using var path = UiTheme.Round(bounds, radius);
        bool primary = Style is UiButtonStyle.Primary or UiButtonStyle.Circle;
        bool active = Selected || hot || pressed || selectionAmount > .001;
        float motionOpacity = Style is UiButtonStyle.Navigation or UiButtonStyle.Tab && !hot && !pressed ? (float)selectionAmount : 1f;
        if (SystemInformation.HighContrast)
        {
            using var fill = new SolidBrush(primary || Selected ? SystemColors.Highlight : SystemColors.Control);
            g.FillPath(fill, path);
            if (active || primary || Style == UiButtonStyle.Secondary)
            { using var outline = new Pen(SystemColors.ControlText); g.DrawPath(outline, path); }
        }
        else if (primary)
        {
            Color top = UiTheme.IsLight ? Color.FromArgb(pressed ? 28 : hot ? 60 : 48, pressed ? 32 : hot ? 65 : 53, pressed ? 37 : hot ? 72 : 60) : pressed ? Color.FromArgb(213, 220, 227) : Color.FromArgb(248, 250, 252);
            Color bottom = UiTheme.IsLight ? Color.FromArgb(31, 36, 43) : hot ? Color.FromArgb(239, 243, 247) : Color.FromArgb(226, 232, 238);
            using var fill = new LinearGradientBrush(bounds, top, bottom, LinearGradientMode.Vertical);
            g.FillPath(fill, path);
            UiTheme.GlassRim(g, bounds, radius, true, s);
        }
        else if (Style == UiButtonStyle.Secondary || active)
        {
            UiTheme.GlassPlate(g, bounds, radius, Selected || hot, s, motionOpacity);
            if (selectionAmount > .001)
            {
                using var selection = new SolidBrush(UiTheme.IsLight ? Color.FromArgb((int)((Style == UiButtonStyle.Tab ? 18 : 14) * selectionAmount), 44, 51, 59) : Color.FromArgb((int)((Style == UiButtonStyle.Tab ? 28 : 21) * selectionAmount), 240, 243, 247));
                g.FillPath(selection, path);
            }
            if (pressed) { using var shade = new SolidBrush(Color.FromArgb(25, 0, 0, 0)); g.FillPath(shade, path); }
            if (selectionAmount > .001 || pressed) UiTheme.GlassRim(g, bounds, radius, Selected || hot, s, motionOpacity);
        }
        Color ink = !Enabled ? UiTheme.Muted : Style switch
        {
            UiButtonStyle.Primary or UiButtonStyle.Circle => UiTheme.PrimaryInk,
            UiButtonStyle.Navigation => selected || hot ? UiTheme.Ink : UiTheme.Muted,
            UiButtonStyle.Quiet or UiButtonStyle.Tab when selected || hot => UiTheme.Ink,
            UiButtonStyle.Tab => UiTheme.Muted,
            _ => ForeColor
        };
        if (SystemInformation.HighContrast) ink = !Enabled ? SystemColors.GrayText : primary || Selected ? SystemColors.HighlightText : SystemColors.ControlText;
        if (IconOnly) UiTheme.Glyph(g, Glyph, ClientRectangle, ink, Style == UiButtonStyle.Circle ? 15 : Style == UiButtonStyle.Navigation ? 15 : 12);
        else if (Style == UiButtonStyle.Navigation && Width < 110 * s)
        {
            UiTheme.Glyph(g, Glyph, new Rectangle(0, (int)(9 * s), Width, (int)(27 * s)), ink, 15);
            using var labelFont = new Font(Font.FontFamily, 8.5f, FontStyle.Regular);
            using var textBrush = new SolidBrush(ink);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
            g.DrawString(Text, labelFont, textBrush, new RectangleF(4 * s, Height - 30 * s, Width - 8 * s, 24 * s), format);
        }
        else
        {
            var textBounds = new Rectangle((int)(12 * s), 0, Math.Max(0, Width - (int)(24 * s)), Height);
            if (Glyph.Length > 0)
            {
                int iconWidth = (int)(22 * s);
                int iconLeft = Style == UiButtonStyle.Primary ? Math.Max((int)(12 * s), (Width - TextRenderer.MeasureText(Text, Font).Width - iconWidth - (int)(10 * s)) / 2) : (int)(14 * s);
                UiTheme.Glyph(g, Glyph, new Rectangle(iconLeft, 0, iconWidth, Height), ink, 12);
                textBounds.X = iconLeft + iconWidth + (int)(10 * s); textBounds.Width = Math.Max(0, Width - textBounds.X - (int)(10 * s));
            }
            using var textBrush = new SolidBrush(ink);
            using var format = new StringFormat { Alignment = Style == UiButtonStyle.Navigation || Glyph.Length > 0 ? StringAlignment.Near : StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; g.DrawString(Text, Font, textBrush, textBounds, format);
        }
        if (Focused && ShowFocusCues) { using var focus = new Pen(SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(190, UiTheme.Accent), Math.Max(1, s)); g.DrawPath(focus, path); }
    }
}

sealed class SurfacePanel : Panel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Radius { get; set; } = 12;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowBorder { get; set; } = true;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool SolidSurface { get; set; }
    public SurfacePanel() { BackColor = UiTheme.Surface; DoubleBuffered = true; ResizeRedraw = true; }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        GlassCanvas.PaintParent(this, e.Graphics);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = UiTheme.Round(new RectangleF(.5f, .5f, Math.Max(1, Width - 1), Math.Max(1, Height - 1)), Radius * DeviceDpi / 96f);
        if (SolidSurface) { using var solid = new SolidBrush(BackColor); e.Graphics.FillPath(solid, path); }
        else { using var fill = new LinearGradientBrush(ClientRectangle, UiTheme.IsLight ? Color.FromArgb(90, 255, 255, 255) : Color.FromArgb(22, 220, 225, 230), UiTheme.IsLight ? Color.FromArgb(48, 255, 255, 255) : Color.FromArgb(9, 180, 187, 195), LinearGradientMode.Vertical); e.Graphics.FillPath(fill, path); }
        if (ShowBorder) { using var border = new Pen(UiTheme.IsLight ? Color.FromArgb(18, 71, 81, 92) : Color.FromArgb(24, 235, 242, 249)); e.Graphics.DrawPath(border, path); }
    }
}

sealed class UiComboBox : ComboBox
{
    bool layered = OperatingSystem.IsWindowsVersionAtLeast(6, 2);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
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
    public UiComboBox()
    {
        FlatStyle = FlatStyle.Flat; BackColor = UiTheme.Elevated; ForeColor = UiTheme.Ink;
        DrawMode = DrawMode.OwnerDrawFixed; DropDownStyle = ComboBoxStyle.DropDownList; IntegralHeight = false; DropDownHeight = 320;
    }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        Color background = (e.State & DrawItemState.Selected) != 0 ? UiTheme.AccentSoft : UiTheme.Elevated;
        using var brush = new SolidBrush(background); e.Graphics.FillRectangle(brush, e.Bounds);
        string text = e.Index >= 0 && e.Index < Items.Count ? GetItemText(Items[e.Index]) ?? "" : Text;
        TextRenderer.DrawText(e.Graphics, text, Font, Rectangle.Inflate(e.Bounds, -8, 0), UiTheme.Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != 0x000F || !IsHandleCreated || IsDisposed) return;
        using var graphics = Graphics.FromHwnd(Handle);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        using (var fill = new SolidBrush(UiTheme.Elevated)) graphics.FillRectangle(fill, ClientRectangle);
        graphics.CompositingMode = CompositingMode.SourceOver;
        int icon = (int)(30 * DeviceDpi / 96f), pad = (int)(10 * DeviceDpi / 96f);
        using var ink = new SolidBrush(ForeColor);
        using var format = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.DrawString(Text, Font, ink, new Rectangle(pad, 0, Math.Max(1, Width - icon - pad), Height), format);
        UiTheme.Glyph(graphics, "\uE70D", new Rectangle(Width - icon, 0, icon, Height), UiTheme.Muted, 8);
    }
}

sealed class UiToggle : CheckBox
{
    public UiToggle() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); Cursor = Cursors.Hand; AutoSize = false; Height = 42; }
    protected override void OnPaint(PaintEventArgs e)
    {
        float s = DeviceDpi / 96f; var g = e.Graphics; g.Clear(BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(0, (Height - 22 * s) / 2, 40 * s, 22 * s);
        using var path = UiTheme.Round(rect, 11 * s);
        using (var fill = new SolidBrush(Checked ? UiTheme.Accent : UiTheme.Border)) g.FillPath(fill, path);
        using (var thumb = new SolidBrush(UiTheme.IsLight ? Color.White : Checked ? UiTheme.Sidebar : UiTheme.Ink)) g.FillEllipse(thumb, rect.X + (Checked ? 21 : 3) * s, rect.Y + 3 * s, 16 * s, 16 * s);
        TextRenderer.DrawText(g, Text, Font, new Rectangle((int)(54 * s), 0, Math.Max(0, Width - (int)(54 * s)), Height), ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -1, -1), UiTheme.Accent, BackColor);
    }
}

sealed class TimelineSlider : Control
{
    int value, minimum, maximum = 100; bool active, hot;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Minimum { get => minimum; set { minimum = value; Value = this.value; } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Maximum { get => maximum; set { maximum = Math.Max(minimum + 1, value); Value = this.value; } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value { get => value; set { int v = Math.Clamp(value, Minimum, Maximum); if (this.value == v) return; this.value = v; Invalidate(); ValueChanged?.Invoke(this, EventArgs.Empty); } }
    public event EventHandler? ValueChanged;
    public event EventHandler? ValueCommitted;
    public TimelineSlider() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true); Height = 24; TabStop = true; Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.Slider; }
    protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    void SetPosition(int x) { float pad = 7 * DeviceDpi / 96f; Value = Minimum + (int)(Math.Clamp((x - pad) / Math.Max(1, Width - 2 * pad), 0, 1) * (Maximum - Minimum)); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { Focus(); Capture = true; active = true; SetPosition(e.X); } base.OnMouseDown(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (active) SetPosition(e.X); base.OnMouseMove(e); }
    protected override void OnMouseUp(MouseEventArgs e) { if (active) { SetPosition(e.X); active = false; Capture = false; ValueCommitted?.Invoke(this, EventArgs.Empty); } base.OnMouseUp(e); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        int step = Math.Max(1, (Maximum - Minimum) / 100);
        if (e.KeyCode is Keys.Left or Keys.Right or Keys.Home or Keys.End) { Value = e.KeyCode switch { Keys.Left => Value - step, Keys.Right => Value + step, Keys.Home => Minimum, _ => Maximum }; ValueCommitted?.Invoke(this, EventArgs.Empty); e.Handled = true; }
        base.OnKeyDown(e);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        float s = DeviceDpi / 96f, pad = 7 * s, mid = Height / 2f;
        float x = pad + (Width - 2 * pad) * (Value - Minimum) / Math.Max(1f, Maximum - Minimum);
        var g = e.Graphics; GlassCanvas.PaintParent(this, g); g.SmoothingMode = SmoothingMode.AntiAlias;
        using var track = new Pen(UiTheme.Border, 2 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var fill = new Pen(UiTheme.Accent, 2 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(track, pad, mid, Math.Max(pad, Width - pad), mid); if (x > pad) g.DrawLine(fill, pad, mid, x, mid);
        if (active || Focused || hot) { using var thumb = new SolidBrush(UiTheme.Ink); g.FillEllipse(thumb, x - 4 * s, mid - 4 * s, 8 * s, 8 * s); }
    }
    protected override AccessibleObject CreateAccessibilityInstance() => new SliderAccessibility(this);
    sealed class SliderAccessibility(TimelineSlider owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.Slider;
        public override string? Value { get => owner.Value.ToString(CultureInfo.InvariantCulture); set { if (int.TryParse(value, out int number)) { owner.Value = number; owner.ValueCommitted?.Invoke(owner, EventArgs.Empty); } } }
    }
}

sealed class CoverPictureBox : PictureBox
{
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var state = e.Graphics.Save();
        e.Graphics.CompositingMode = CompositingMode.SourceCopy;
        using var fill = new SolidBrush(BackColor);
        e.Graphics.FillRectangle(fill, ClientRectangle);
        e.Graphics.Restore(state);
    }
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width <= 1 || Height <= 1) return;
        using var path = UiTheme.Round(new RectangleF(0, 0, Width, Height), 7 * DeviceDpi / 96f);
        var previous = Region; Region = new Region(path); previous?.Dispose();
    }
}

sealed record MediaRow(Media Media, string Detail = "", int Ordinal = 0) { public override string ToString() => Media.Title; }

sealed class UiListBox : ListBox
{
    Font? secondary;
    NativeTintSurface? rowSurface;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<int, MediaRow, string>? RowDetail { get; set; }
    public UiListBox() { BackColor = UiTheme.Surface; ForeColor = UiTheme.Ink; BorderStyle = BorderStyle.None; IntegralHeight = false; DrawMode = DrawMode.OwnerDrawFixed; ItemHeight = 54; }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ItemHeight = (int)(54 * DeviceDpi / 96f); }
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == 0x000F && IsHandleCreated && !IsDisposed)
        {
            // A far-offscreen LB_GETITEMRECT can return unusable coordinates.
            // In an owner-drawn fixed-height list, derive only the empty area
            // from the visible top row; never paint over already drawn items.
            int bottom = (int)Math.Min(ClientSize.Height, Math.Max(0L, ((long)Items.Count - TopIndex) * ItemHeight));
            if (bottom < ClientSize.Height)
            {
                using var graphics = Graphics.FromHwnd(Handle);
                graphics.SetClip(new Rectangle(0, bottom, ClientSize.Width, ClientSize.Height - bottom));
                GlassCanvas.PaintParent(this, graphics);
            }
        }
    }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); secondary?.Dispose(); secondary = new Font(Font.FontFamily, Math.Max(8, Font.Size - 1)); }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        using var measurement = UiPerformance.MeasurePaint("paint_queue_item");
        // A single bounded scratch row, reused rather than one bitmap per item.
        // Render text/fills offscreen and submit once to the DWM-backed HWND.
        var size = e.Bounds.Size;
        if (size.Width > 0 && size.Height > 0 && (long)size.Width * size.Height * 4 <= 4L * 1024 * 1024)
        {
            if (rowSurface?.Image.Size != size)
            {
                rowSurface?.Dispose(); rowSurface = NativeTintSurface.TryCreate(size);
                rowSurface?.Image.SetResolution(DeviceDpi, DeviceDpi);
            }
            if (rowSurface != null)
            {
                using (var paint = Graphics.FromImage(rowSurface.Image))
                {
                    PaintRow(paint, new Rectangle(Point.Empty, size), e.Index, e.State);
                    paint.Flush(FlushIntention.Sync);
                }
                var state = e.Graphics.Save();
                try
                {
                    e.Graphics.SetClip(e.Bounds, CombineMode.Intersect);
                    e.Graphics.TranslateTransform(e.Bounds.X, e.Bounds.Y);
                    e.Graphics.CompositingMode = CompositingMode.SourceCopy;
                    if (!rowSurface.TryPaint(e.Graphics, Point.Empty)) e.Graphics.DrawImageUnscaled(rowSurface.Image, 0, 0);
                }
                finally { e.Graphics.Restore(state); }
                return;
            }
        }
        PaintRow(e.Graphics, e.Bounds, e.Index, e.State);
    }
    void PaintRow(Graphics g, Rectangle bounds, int index, DrawItemState state)
    {
        float s = DeviceDpi / 96f; g.SmoothingMode = SmoothingMode.AntiAlias;
        bool selected = (state & DrawItemState.Selected) != 0;
        using (var brush = new SolidBrush(BackColor)) g.FillRectangle(brush, bounds);
        if (selected) { using var fill = new SolidBrush(UiTheme.AccentSoft); using var path = UiTheme.Round(Rectangle.Inflate(bounds, -(int)(3 * s), -(int)(3 * s)), 7 * s); g.FillPath(fill, path); }
        var row = Items[index] as MediaRow;
        string text = Items[index].ToString() ?? "", detail = row == null ? "" : RowDetail?.Invoke(index, row) ?? row.Detail;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var format = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        void Draw(string value, Font font, Rectangle bounds, Color color, bool center = false)
        {
            format.Alignment = center ? StringAlignment.Center : StringAlignment.Near;
            using var ink = new SolidBrush(color); g.DrawString(value, font, ink, bounds, format);
        }
        var numberBounds = new Rectangle(bounds.X, bounds.Y, (int)(38 * s), bounds.Height);
        Draw((row?.Ordinal > 0 ? row.Ordinal : index + 1).ToString("00"), secondary ?? Font, numberBounds, selected ? UiTheme.Accent : UiTheme.Muted, center: true);
        int top = bounds.Y + (int)((detail.Length > 0 ? 8 : 17) * s), left = bounds.X + (int)(42 * s);
        Draw(text, Font, new Rectangle(left, top, Math.Max(0, bounds.Width - (int)(52 * s)), (int)(23 * s)), selected ? UiTheme.Ink : ForeColor);
        if (detail.Length > 0) Draw(detail, secondary ?? Font, new Rectangle(left, bounds.Y + (int)(30 * s), Math.Max(0, bounds.Width - (int)(52 * s)), (int)(19 * s)), UiTheme.Muted);
    }
    protected override void Dispose(bool disposing) { if (disposing) { secondary?.Dispose(); rowSurface?.Dispose(); rowSurface = null; } base.Dispose(disposing); }
}
