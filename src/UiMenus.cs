// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

namespace LocalMedia;

static class UiMenus
{
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ContextMenuStrip, MenuRelease> releases = new();

    // WinForms still visits the parent drop-down after raising Closed (notably
    // when clicking a nested item). Disposing inside that event invalidates the
    // handle while ToolStripDropDown.OnItemClicked is unwinding.
    public static void DisposeAfterClose(ContextMenuStrip menu, Control owner)
    {
        if (!menu.IsDisposed) releases.GetValue(menu, value => new MenuRelease(value, owner)).AfterClose();
    }

    // A media command can rebuild its source page during Click. Controls sharing
    // that context menu detach immediately, but its native menu lives until the
    // current event returns. Weak identity tracking deduplicates shared owners.
    public static void DisposeDeferred(ContextMenuStrip menu, Control owner)
    {
        if (!menu.IsDisposed) releases.GetValue(menu, value => new MenuRelease(value, owner)).Queue();
    }

    sealed class MenuRelease
    {
        readonly ContextMenuStrip menu;
        readonly Control owner;
        bool waitingForClose, queued, released;
        public MenuRelease(ContextMenuStrip menu, Control owner)
        {
            this.menu = menu; this.owner = owner;
            menu.Disposed += MenuDisposed;
            owner.Disposed += OwnerDisposed;
        }
        public void AfterClose()
        {
            if (waitingForClose || queued || released) return;
            waitingForClose = true;
            menu.Closed += Closed;
        }
        void Detach()
        {
            menu.Closed -= Closed;
            menu.Disposed -= MenuDisposed;
            owner.Disposed -= OwnerDisposed;
            releases.Remove(menu);
        }
        void Release()
        {
            if (released) return;
            released = true;
            Detach();
            if (!menu.IsDisposed) menu.Dispose();
        }
        void MenuDisposed(object? sender, EventArgs args)
        {
            released = true;
            Detach();
        }
        void OwnerDisposed(object? sender, EventArgs args) => Release();
        void Closed(object? sender, ToolStripDropDownClosedEventArgs args) => Queue();
        public void Queue()
        {
            if (queued || released) return;
            queued = true;
            menu.Closed -= Closed;
            // Never ask a disposed/closing owner to create a new handle. The
            // owner-disposal hook also covers a posted callback lost at exit.
            if (owner.IsDisposed || owner.Disposing || !owner.IsHandleCreated) { Release(); return; }
            try { owner.BeginInvoke((Action)Release); }
            catch (InvalidOperationException) { Release(); }
        }
    }

    public static void Initialize()
    {
        // Default context menus and their drop-downs share the manager renderer.
        // Leave the Windows renderer untouched when high contrast is active.
        if (!SystemInformation.HighContrast)
            ToolStripManager.Renderer = new NeutralMenuRenderer();
    }

    sealed class NeutralMenuColors : ProfessionalColorTable
    {
        internal static Color Selection => UiTheme.AccentSoft;
        internal static Color Disabled => UiTheme.Muted;

        public NeutralMenuColors() => UseSystemColors = false;
        public override Color ToolStripDropDownBackground => UiTheme.Surface;
        public override Color ToolStripBorder => UiTheme.Border;
        public override Color MenuBorder => UiTheme.Border;
        public override Color MenuItemBorder => Selection;
        public override Color MenuItemSelected => Selection;
        public override Color MenuItemSelectedGradientBegin => Selection;
        public override Color MenuItemSelectedGradientEnd => Selection;
        public override Color MenuItemPressedGradientBegin => Selection;
        public override Color MenuItemPressedGradientMiddle => Selection;
        public override Color MenuItemPressedGradientEnd => Selection;
        public override Color MenuStripGradientBegin => UiTheme.Surface;
        public override Color MenuStripGradientEnd => UiTheme.Surface;
        public override Color ImageMarginGradientBegin => UiTheme.Surface;
        public override Color ImageMarginGradientMiddle => UiTheme.Surface;
        public override Color ImageMarginGradientEnd => UiTheme.Surface;
        public override Color ImageMarginRevealedGradientBegin => UiTheme.Surface;
        public override Color ImageMarginRevealedGradientMiddle => UiTheme.Surface;
        public override Color ImageMarginRevealedGradientEnd => UiTheme.Surface;
        public override Color CheckBackground => UiTheme.Surface;
        public override Color CheckSelectedBackground => Selection;
        public override Color CheckPressedBackground => Selection;
        public override Color SeparatorDark => UiTheme.Border;
        public override Color SeparatorLight => UiTheme.Border;
    }

    sealed class NeutralMenuRenderer : ToolStripProfessionalRenderer
    {
        readonly ToolStripSystemRenderer system = new();

        public NeutralMenuRenderer() : base(new NeutralMenuColors()) => RoundedEdges = false;

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            if (SystemInformation.HighContrast) { system.DrawToolStripBackground(e); return; }
            using var fill = new SolidBrush(UiTheme.Surface);
            e.Graphics.FillRectangle(fill, e.AffectedBounds);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            if (SystemInformation.HighContrast) { system.DrawImageMargin(e); return; }
            using var fill = new SolidBrush(UiTheme.Surface);
            e.Graphics.FillRectangle(fill, e.AffectedBounds);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (SystemInformation.HighContrast) { system.DrawMenuItemBackground(e); return; }
            if (!e.Item.Enabled || (!e.Item.Selected && !e.Item.Pressed)) return;
            using var fill = new SolidBrush(NeutralMenuColors.Selection);
            e.Graphics.FillRectangle(fill, Rectangle.Inflate(new Rectangle(Point.Empty, e.Item.Size), -2, -1));
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (SystemInformation.HighContrast)
            {
                e.TextColor = !e.Item.Enabled ? SystemColors.GrayText : e.Item.Selected ? SystemColors.HighlightText : SystemColors.MenuText;
                system.DrawItemText(e); return;
            }
            e.TextColor = e.Item.Enabled ? UiTheme.Ink : NeutralMenuColors.Disabled;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            bool enabled = e.Item?.Enabled ?? true;
            bool selected = e.Item?.Selected ?? false;
            if (SystemInformation.HighContrast)
            {
                e.ArrowColor = !enabled ? SystemColors.GrayText : selected ? SystemColors.HighlightText : SystemColors.MenuText;
                system.DrawArrow(e); return;
            }
            e.ArrowColor = enabled ? UiTheme.Ink : NeutralMenuColors.Disabled;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            if (SystemInformation.HighContrast) { system.DrawItemCheck(e); return; }
            Color background = e.Item.Enabled && (e.Item.Selected || e.Item.Pressed) ? NeutralMenuColors.Selection : UiTheme.Surface;
            Color ink = e.Item.Enabled ? UiTheme.Ink : NeutralMenuColors.Disabled;
            if (e.Item is ToolStripMenuItem { CheckState: CheckState.Indeterminate })
            {
                using var pen = new Pen(ink, Math.Max(1, (e.ToolStrip?.DeviceDpi ?? 96) / 96f));
                int middle = e.ImageRectangle.Top + e.ImageRectangle.Height / 2;
                e.Graphics.DrawLine(pen, e.ImageRectangle.Left + e.ImageRectangle.Width / 4, middle, e.ImageRectangle.Right - e.ImageRectangle.Width / 4, middle);
            }
            else
                ControlPaint.DrawMenuGlyph(e.Graphics, e.ImageRectangle, MenuGlyph.Checkmark, ink, background);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            if (SystemInformation.HighContrast) { system.DrawSeparator(e); return; }
            float scale = (e.ToolStrip?.DeviceDpi ?? 96) / 96f;
            int inset = Math.Max(4, (int)(8 * scale));
            using var pen = new Pen(UiTheme.Border, Math.Max(1, scale));
            if (e.Vertical)
                e.Graphics.DrawLine(pen, e.Item.Width / 2, inset, e.Item.Width / 2, Math.Max(inset, e.Item.Height - inset));
            else
                e.Graphics.DrawLine(pen, inset, e.Item.Height / 2, Math.Max(inset, e.Item.Width - inset), e.Item.Height / 2);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (SystemInformation.HighContrast) { system.DrawToolStripBorder(e); return; }
            if (e.ToolStrip is ToolStripDropDown && e.ToolStrip.Width > 1 && e.ToolStrip.Height > 1)
            {
                using var pen = new Pen(UiTheme.Border);
                e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            }
        }
    }
}
