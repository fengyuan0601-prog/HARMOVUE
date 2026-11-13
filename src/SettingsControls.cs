// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace LocalMedia;

sealed class SettingsToggle : Control
{
    bool isChecked, spacePressed;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => isChecked;
        set
        {
            if (isChecked == value) return;
            isChecked = value; Invalidate();
            if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public event EventHandler? CheckedChanged;
    public SettingsToggle()
    {
        AutoSize = false; Size = new Size(52, 32); Cursor = Cursors.Hand; TabStop = true;
        AccessibleRole = AccessibleRole.CheckButton;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick | ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
        BackColor = Color.Transparent;
    }
    protected override void OnClick(EventArgs e)
    {
        if (Enabled) { Focus(); Checked = !Checked; }
        base.OnClick(e);
    }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) Focus(); base.OnMouseDown(e); }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Handle Space here before the form's global playback shortcut sees it.
        if (keyData == Keys.Space && Enabled) { spacePressed = true; return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    protected override bool IsInputKey(Keys keyData) => keyData == Keys.Space || base.IsInputKey(keyData);
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space && spacePressed)
        {
            spacePressed = false; e.Handled = true;
            if (Enabled) OnClick(EventArgs.Empty);
        }
        base.OnKeyUp(e);
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { spacePressed = false; base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnPaintBackground(PaintEventArgs e) => GlassCanvas.PaintParent(this, e.Graphics);
    protected override void OnPaint(PaintEventArgs e)
    {
        float scale = DeviceDpi / 96f;
        var bounds = new RectangleF((Width - 44 * scale) / 2, (Height - 24 * scale) / 2, 44 * scale, 24 * scale);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = UiTheme.Round(bounds, 12 * scale);
        using (var track = new SolidBrush(Checked ? UiTheme.Accent : UiTheme.Border)) e.Graphics.FillPath(track, path);
        using (var thumb = new SolidBrush(Checked ? UiTheme.Surface : UiTheme.Ink))
            e.Graphics.FillEllipse(thumb, bounds.X + (Checked ? 23 : 3) * scale, bounds.Y + 3 * scale, 18 * scale, 18 * scale);
        if (Focused && ShowFocusCues) { using var focus = new Pen(UiTheme.Ink, scale); e.Graphics.DrawRectangle(focus, 1, 1, Width - 3, Height - 3); }
    }
    protected override AccessibleObject CreateAccessibilityInstance() => new ToggleAccessibility(this);
    sealed class ToggleAccessibility(SettingsToggle owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.CheckButton;
        public override AccessibleStates State => base.State | AccessibleStates.Focusable | (owner.Checked ? AccessibleStates.Checked : AccessibleStates.None);
        public override string? DefaultAction => owner.Checked ? "关闭" : "开启";
        public override void DoDefaultAction() { if (owner.Enabled) owner.OnClick(EventArgs.Empty); }
    }
}

sealed class SettingsRow : GlassCanvas
{
    readonly Label heading, detail;
    readonly Control? action;
    readonly int actionWidth;
    public SettingsRow(Label heading, Label detail, Control? action, int actionWidth)
    {
        this.heading = heading; this.detail = detail; this.action = action; this.actionWidth = actionWidth;
        heading.AutoEllipsis = detail.AutoEllipsis = false;
        Controls.AddRange([heading, detail]); if (action != null) Controls.Add(action);
    }
    public int Arrange(int width, float scale)
    {
        int P(float value) => (int)(value * scale);
        int rightWidth = action == null ? 0 : P(actionWidth);
        int textWidth = Math.Max(P(100), width - rightWidth - (action == null ? 0 : P(28)));
        using var graphics = CreateGraphics();
        int headingHeight = Math.Max(P(24), (int)Math.Ceiling(graphics.MeasureString(heading.Text, heading.Font, textWidth).Height));
        int detailHeight = detail.Text.Length == 0 ? 0 : (int)Math.Ceiling(graphics.MeasureString(detail.Text, detail.Font, textWidth).Height);
        int height = Math.Max(P(detailHeight > 0 ? 74 : 56), headingHeight + detailHeight + P(detailHeight > 0 ? 30 : 24));
        heading.SetBounds(0, P(12), textWidth, headingHeight);
        detail.SetBounds(0, P(15) + headingHeight, textWidth, detailHeight);
        if (action != null) action.SetBounds(width - rightWidth, (height - P(36)) / 2, rightWidth, P(36));
        return height;
    }
}

sealed class SettingsActionGroup : GlassCanvas
{
    readonly List<(Control Button, int Width)> buttons = [];
    public void Add(Control button, int width) { buttons.Add((button, width)); Controls.Add(button); }
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        float scale = DeviceDpi / 96f; int x = 0;
        foreach (var entry in buttons)
        {
            int width = (int)(entry.Width * scale);
            entry.Button.SetBounds(x, 0, width, Height); x += width + (int)(8 * scale);
        }
    }
}

sealed class SettingsDialog : Form
{
    readonly Font headingFont;
    readonly Panel footer;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Panel Body { get; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public UiButton SaveButton { get; }
    public SettingsDialog(string title, string description, Size size, float scale, Font font, string acceptText = "保存")
    {
        int P(float value) => (int)(value * scale);
        Text = title; Icon = Brand.AppIcon(); ShowIcon = false; Font = font; AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(P(size.Width), P(size.Height)); BackColor = UiTheme.Surface; ForeColor = UiTheme.Ink;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; Padding = new Padding(P(28));
        headingFont = new Font(font.FontFamily, 18);
        var header = new Panel { Dock = DockStyle.Top, Height = P(description.Length == 0 ? 54 : 88), BackColor = BackColor };
        var heading = new Label { Text = title, Font = headingFont, ForeColor = UiTheme.Ink, Dock = DockStyle.Top, Height = P(36) };
        var hint = new Label { Text = description, Font = font, ForeColor = UiTheme.Muted, Dock = DockStyle.Fill };
        header.Controls.Add(hint); header.Controls.Add(heading);
        Body = new Panel { Dock = DockStyle.Fill, BackColor = BackColor };
        footer = new Panel { Dock = DockStyle.Bottom, Height = P(60), BackColor = BackColor };
        SaveButton = new UiButton { Text = acceptText, Style = UiButtonStyle.Primary, DialogResult = DialogResult.OK, AccessibleName = acceptText };
        var cancel = new UiButton { Text = "取消", Style = UiButtonStyle.Quiet, DialogResult = DialogResult.Cancel, AccessibleName = "取消" };
        footer.Controls.AddRange([SaveButton, cancel]);
        void Arrange() { SaveButton.SetBounds(footer.Width - P(96), P(24), P(96), P(36)); cancel.SetBounds(footer.Width - P(204), P(24), P(96), P(36)); }
        footer.Resize += (_, _) => Arrange();
        Controls.Add(Body); Controls.Add(footer); Controls.Add(header);
        AcceptButton = SaveButton; CancelButton = cancel; Arrange();
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UiTheme.WindowChrome(this, false); }
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) headingFont.Dispose(); }
}

sealed class SettingsChoiceList : ListBox
{
    public SettingsChoiceList()
    {
        BorderStyle = BorderStyle.None; BackColor = UiTheme.Surface; ForeColor = UiTheme.Ink;
        DrawMode = DrawMode.OwnerDrawVariable; IntegralHeight = false;
    }
    protected override void OnMeasureItem(MeasureItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        float scale = DeviceDpi / 96f;
        e.ItemHeight = Math.Max((int)(48 * scale), (int)Math.Ceiling(e.Graphics.MeasureString(Items[e.Index].ToString(), Font, Math.Max(40, Width - (int)(40 * scale))).Height + 24 * scale));
    }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        float scale = DeviceDpi / 96f;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var background = new SolidBrush(selected ? UiTheme.Elevated : BackColor)) e.Graphics.FillRectangle(background, e.Bounds);
        using var ink = new SolidBrush(ForeColor);
        using var format = new StringFormat { LineAlignment = StringAlignment.Center };
        e.Graphics.DrawString(Items[e.Index].ToString(), Font, ink, new RectangleF(e.Bounds.X + 16 * scale, e.Bounds.Y, e.Bounds.Width - 32 * scale, e.Bounds.Height), format);
        if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
    }
}

sealed class ShortcutCapture : TextBox
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Keys Binding { get; private set; }
    public ShortcutCapture(Keys binding)
    {
        Binding = binding; Text = binding.ToString(); ReadOnly = true; BorderStyle = BorderStyle.FixedSingle;
        BackColor = UiTheme.Elevated; ForeColor = UiTheme.Ink; ShortcutsEnabled = false;
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((keyData & Keys.KeyCode) is Keys.Tab or Keys.Escape) return base.ProcessCmdKey(ref msg, keyData);
        if ((keyData & Keys.KeyCode) is not Keys.ControlKey and not Keys.ShiftKey and not Keys.Menu)
        { Binding = keyData; Text = keyData.ToString(); SelectAll(); }
        return true;
    }
}
