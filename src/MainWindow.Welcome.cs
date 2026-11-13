// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

namespace LocalMedia;

public sealed partial class MainWindow
{
    // One first-run experience across every library view. No sample media is
    // inserted to decorate the user's library; every visible action is real.
    Action AddLibraryEmptyState(Control host, string title)
    {
        bool videoLibrary = page == "视频库";
        var card = new GlassCanvas { Material = GlassMaterial.Card, CornerRadius = 28, AccessibleName = title };
        var heading = Caption(videoLibrary ? "还没有视频" : "还没有音乐", 30);
        var spaciousHeadingFont = heading.Font;
        var compactHeadingFont = OwnedFont(26);
        heading.AutoEllipsis = false;
        var add = (UiButton)Button("添加目录", AddRoot, 156, 44);
        add.Style = UiButtonStyle.Primary; add.Glyph = UiTheme.Add; add.Font = OwnedFont(10);
        var open = (UiButton)Button("打开文件", OpenFiles, 116, 44);
        open.Style = UiButtonStyle.Quiet; open.Font = OwnedFont(10);
        var drop = new LibraryDropSurface { Material = GlassMaterial.Card, CornerRadius = 24, Cursor = Cursors.Hand, AllowDrop = true, TabStop = true, AccessibleRole = AccessibleRole.PushButton, AccessibleName = "拖入影音文件或文件夹，也可点击打开文件" };
        var icon = new LibraryWelcomeGlyph { Glyph = videoLibrary ? UiTheme.Video : UiTheme.Music, Cursor = Cursors.Hand };
        var dropTitle = Caption("拖入文件", 11.5f); dropTitle.TextAlign = ContentAlignment.MiddleCenter;
        drop.Controls.AddRange([icon, dropTitle]);
        tips.SetToolTip(drop, "打开文件 · 支持拖入影音文件和文件夹");
        tips.SetToolTip(icon, "打开文件 · 支持拖入影音文件和文件夹");
        foreach (Control target in new Control[] { card, heading, drop, icon, dropTitle })
        {
            target.AllowDrop = true;
            if (target == drop || target == icon || target == dropTitle)
            {
                target.Cursor = Cursors.Hand;
                target.Click += (_, _) => Try(OpenFiles);
            }
            target.DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
            target.DragDrop += async (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) await OpenPaths(paths); };
        }
        card.Controls.AddRange([heading, add, open, drop]);
        host.Controls.Add(card);
        void Arrange()
        {
            if (host.IsDisposed || dpiReady && !dpiScaled.Contains(host)) return;
            float scale = dpiReady ? dpiFactor : 1; int P(float n) => (int)(n * scale);
            int width = Math.Min(P(1000), Math.Max(P(260), host.ClientSize.Width - P(8)));
            bool twoColumns = width >= P(660);
            int height = Math.Max(1, Math.Min(P(360), host.ClientSize.Height - P(16)));
            int top = Math.Max(P(8), (host.ClientSize.Height - height) / 4);
            bool shortCard = height < P(344);
            card.SetBounds(Math.Max(0, (host.ClientSize.Width - width) / 2), top, width, height);
            int inset = P(width >= P(840) ? 44 : 30);
            int leftWidth = twoColumns ? width - P(330) - inset : width - inset * 2;
            heading.Font = shortCard ? compactHeadingFont : spaciousHeadingFont;
            int headingTop = shortCard ? Math.Max(P(20), (height - P(126)) / 2) : P(94);
            heading.SetBounds(inset - P(2), headingTop, leftWidth, P(56));
            int actionsTop = headingTop + P(shortCard ? 82 : 90);
            add.SetBounds(inset, actionsTop, P(156), P(44));
            open.SetBounds(inset + P(168), actionsTop, Math.Min(P(116), Math.Max(P(84), leftWidth - P(168))), P(44));
            drop.Visible = twoColumns;
            if (twoColumns)
            {
                drop.SetBounds(width - P(290), P(34), P(256), height - P(68));
                int middle = drop.Height / 2;
                icon.SetBounds(drop.Width / 2 - P(40), middle - P(64), P(80), P(80));
                dropTitle.SetBounds(P(12), middle + P(30), drop.Width - P(24), P(28));
            }
        }
        host.SizeChanged += (_, _) => Arrange();
        if (IsHandleCreated) BeginInvoke((Action)Arrange);
        return Arrange;
    }
}

sealed class LibraryDropSurface : GlassCanvas
{
    public LibraryDropSurface() => SetStyle(ControlStyles.Selectable, true);
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) Focus(); base.OnMouseDown(e); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is Keys.Enter or Keys.Space) { OnClick(EventArgs.Empty); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!Focused || !ShowFocusCues) return;
        using var path = UiTheme.Round(new RectangleF(2, 2, Width - 4, Height - 4), CornerRadius * DeviceDpi / 96f);
        using var pen = new Pen(UiTheme.Ink, DeviceDpi / 96f);
        e.Graphics.DrawPath(pen, path);
    }
}

// The same installed icon font as navigation, not a fabricated album cover.
sealed class LibraryWelcomeGlyph : Control
{
    public string Glyph = UiTheme.Music;
    public LibraryWelcomeGlyph()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "媒体库";
    }
    protected override void OnPaintBackground(PaintEventArgs e) => GlassCanvas.PaintParent(this, e.Graphics);
    protected override void OnPaint(PaintEventArgs e) => UiTheme.Glyph(e.Graphics, Glyph, ClientRectangle, UiTheme.Ink, 38);
}
