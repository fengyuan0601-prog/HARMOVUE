// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace LocalMedia;

// Bounded cache of a material-bearing canvas's fully composed static
// ARGB surface: root tint plus ancestor/current plates. It is never a window
// screenshot or a cache of children, text, artwork or video. All access is on
// the UI paint thread; a DIB and its Bitmap wrapper share one pixel allocation.
static class MaterialSurfaceCache
{
    public const int MaximumEntries = 4;
    public const long MaximumBitmapBytes = 8L * 1024 * 1024;
    public const long MaximumBytes = 16L * 1024 * 1024;
    public const long MaximumCombinedTintBytes = GlassBackdropCache.MaximumBytes + MaximumBytes;
    public static bool Enabled { get; } = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LOCALMEDIA_DATA"))
        || Environment.GetEnvironmentVariable("LOCALMEDIA_MATERIAL_CACHE") != "0";

    static readonly Dictionary<GlassCanvas, Entry> entries = [];
    static long sequence, allocatedBytes;
    static PaletteKey? palette;
    readonly record struct PaletteKey(bool Light, int Top, int Bottom, int Surface, int Border, int Ink);
    readonly record struct SurfaceKey(Control Root, Size RootSize, int RootDpi, Point RootOffset, Size Size, int Dpi, PaletteKey Palette);
    readonly record struct AncestorKey(Control Control, Control? Parent, Point Location, Size Size, int Dpi, int BackColor,
        GlassMaterial Material, bool Floating, Padding Inset, int Radius);

    sealed class Snapshot
    {
        public required SurfaceKey Surface;
        public required AncestorKey[] Ancestors;
        public bool Matches(Snapshot other)
        {
            if (Surface != other.Surface || Ancestors.Length != other.Ancestors.Length) return false;
            for (int i = 0; i < Ancestors.Length; i++) if (Ancestors[i] != other.Ancestors[i]) return false;
            return true;
        }
    }
    sealed class Entry
    {
        public required GlassCanvas Anchor;
        public required EventHandler GeometryChanged;
        public required EventHandler Closed;
        public NativeTintSurface? Surface;
        public Snapshot? Snapshot, Failed;
        public long Bytes, LastUse;
    }

    public static bool TryPaint(Control panel, Graphics graphics)
    {
        if (!Enabled || !NativeTintSurface.Enabled || panel.IsDisposed) return false;
        using var attempt = UiPerformance.MeasurePaint("paint_material_cache_attempt");
        if (SystemInformation.HighContrast)
        {
            foreach (var value in entries.Values) DropSurface(value);
            palette = null;
            return Reject("high_contrast");
        }
        GlassCanvas? anchor = null;
        Point panelOffset = Point.Empty;
        for (Control? current = panel; current != null; current = current.Parent)
        {
            if (current is GlassCanvas canvas && (canvas.Material != GlassMaterial.Window || canvas.Floating))
            { anchor = canvas; break; }
            if (current.Parent != null) panelOffset.Offset(current.Left, current.Top);
        }
        // Window-only descendants already use the existing root tint cache.
        if (anchor == null) return false;
        if (anchor.IsDisposed || !anchor.IsHandleCreated || anchor.BackColor.R != 0 || anchor.BackColor.G != 0 || anchor.BackColor.B != 0)
            return Reject("anchor");
        Size size = anchor.ClientSize;
        long bytes = (long)size.Width * size.Height * 4;
        if (size.Width <= 0 || size.Height <= 0 || bytes > MaximumBitmapBytes)
        {
            Invalidate(anchor);
            return Reject("size");
        }
        // NativeTint's intersection behaviour is sufficient for a root image,
        // but a smaller material image must cover the WHOLE requested clip.
        // Otherwise return to the direct path before changing any pixels.
        if (!CoversClip(graphics, panelOffset, size)) return Reject("clip");

        GlassCanvas.BackdropParameters(anchor, out var root, out var rootOffset, out var top, out var bottom);
        if (root.IsDisposed || !root.IsHandleCreated) return Reject("root");
        var nextPalette = new PaletteKey(UiTheme.IsLight, top.ToArgb(), bottom.ToArgb(),
            UiTheme.Surface.ToArgb(), UiTheme.Border.ToArgb(), UiTheme.Ink.ToArgb());
        // GlassPlate's actual light/dark constants are fixed for the life of
        // this process. If runtime material sliders are added, their revision
        // or effective colours MUST become part of this key as well.
        if (palette != nextPalette)
        {
            foreach (var value in entries.Values) DropSurface(value);
            palette = nextPalette;
        }
        var ancestors = new List<AncestorKey>();
        for (Control? current = anchor; current != null; current = current.Parent)
        {
            var canvas = current as GlassCanvas;
            ancestors.Add(new AncestorKey(current, current.Parent, current.Parent == null ? Point.Empty : current.Location,
                current.ClientSize, current.DeviceDpi, current.BackColor.ToArgb(), canvas?.Material ?? GlassMaterial.Window,
                canvas?.Floating ?? false, canvas?.SurfaceInset ?? Padding.Empty, canvas?.CornerRadius ?? 0));
        }
        var snapshot = new Snapshot
        {
            Surface = new SurfaceKey(root, root.ClientSize, root.DeviceDpi, rootOffset, size, anchor.DeviceDpi, nextPalette),
            Ancestors = ancestors.ToArray()
        };
        if (!entries.TryGetValue(anchor, out var entry))
        {
            if (entries.Count >= MaximumEntries) Remove(entries.Values.MinBy(value => value.LastUse)!.Anchor);
            entry = new Entry
            {
                Anchor = anchor,
                GeometryChanged = (_, _) => Invalidate(anchor),
                Closed = (_, _) => Remove(anchor)
            };
            entries.Add(anchor, entry);
            anchor.SizeChanged += entry.GeometryChanged;
            anchor.ParentChanged += entry.GeometryChanged;
            anchor.Disposed += entry.Closed;
        }
        entry.LastUse = ++sequence;
        if (entry.Surface == null || entry.Snapshot?.Matches(snapshot) != true)
        {
            if (entry.Failed?.Matches(snapshot) == true) return Reject("failed_key");
            DropSurface(entry);
            // Evict BEFORE allocating; rebuilds do not briefly hold both the
            // old and new pixels outside the 16 MiB material-cache budget.
            while (allocatedBytes + bytes > MaximumBytes)
            {
                var oldest = entries.Values.Where(value => value.Surface != null && !ReferenceEquals(value, entry))
                    .MinBy(value => value.LastUse);
                if (oldest == null) return Reject("budget");
                Remove(oldest.Anchor);
            }
            NativeTintSurface? surface = null;
            bool reserved = false;
            try
            {
                using var build = UiPerformance.Measure("material_cache_build", count: (int)bytes);
                surface = NativeTintSurface.TryCreate(size);
                if (surface == null) { entry.Failed = snapshot; return Reject("allocation"); }
                allocatedBytes += bytes; reserved = true;
                surface.Image.SetResolution(Math.Max(1, anchor.DeviceDpi), Math.Max(1, anchor.DeviceDpi));
                using (var paint = Graphics.FromImage(surface.Image))
                {
                    paint.PageUnit = GraphicsUnit.Pixel;
                    paint.SetClip(new Rectangle(Point.Empty, size));
                    // Bypass both material and root raster caches while building:
                    // render the same vector source once into these shared pixels.
                    GlassCanvas.PaintStaticMaterial(anchor, paint, useBackdropCache: false);
                    paint.Flush(FlushIntention.Sync);
                }
                entry.Surface = surface; surface = null;
                entry.Snapshot = snapshot; entry.Bytes = bytes; reserved = false;
                UiPerformance.Record("material_cache_bytes", 0, count: checked((int)allocatedBytes));
            }
            catch (Exception error) when (error is ArgumentException or OutOfMemoryException or ExternalException or InvalidOperationException)
            {
                entry.Failed = snapshot;
                UiPerformance.Record("material_cache_build_failure", 0, error.GetType().Name);
                return false;
            }
            finally
            {
                surface?.Dispose();
                if (reserved) allocatedBytes -= bytes;
            }
        }
        using (UiPerformance.MeasurePaint("paint_material_cache_copy"))
        {
            // Strict native transform/DC validation remains in NativeTintSurface.
            // An unsupported context takes the original vector path, not a slow
            // GDI+ alpha-image blit and not a partially composed transparent plate.
            if (entry.Surface!.TryPaint(graphics, panelOffset)) return true;
        }
        return Reject("native_context");
    }

    static bool CoversClip(Graphics graphics, Point offset, Size size)
    {
        try
        {
            if (graphics.CompositingMode != CompositingMode.SourceCopy || graphics.CompositingQuality != CompositingQuality.Default
                || graphics.PixelOffsetMode is PixelOffsetMode.Half or PixelOffsetMode.HighQuality) return false;
            using var clip = graphics.Clip;
            using var identity = new Matrix();
            var rectangles = clip.GetRegionScans(identity);
            if (rectangles.Length == 0) return true;
            if (rectangles.Length != 1) return false;
            RectangleF requested = rectangles[0];
            return float.IsFinite(requested.Left) && float.IsFinite(requested.Top) && float.IsFinite(requested.Right) && float.IsFinite(requested.Bottom)
                && requested.Width >= 0 && requested.Height >= 0
                && requested.Left >= -(long)offset.X && requested.Top >= -(long)offset.Y
                && requested.Right <= (long)size.Width - offset.X && requested.Bottom <= (long)size.Height - offset.Y;
        }
        catch (Exception error) when (error is ArgumentException or ExternalException or InvalidOperationException or OutOfMemoryException)
        { return false; }
    }
    static bool Reject(string reason)
    {
        using var count = UiPerformance.MeasurePaint("material_cache_fallback_" + reason);
        return false;
    }
    static void Invalidate(GlassCanvas anchor)
    {
        if (entries.TryGetValue(anchor, out var entry)) DropSurface(entry);
    }
    static void DropSurface(Entry entry)
    {
        entry.Surface?.Dispose(); entry.Surface = null;
        allocatedBytes -= entry.Bytes; entry.Bytes = 0;
        entry.Snapshot = null; entry.Failed = null;
    }
    static void Remove(GlassCanvas anchor)
    {
        if (!entries.Remove(anchor, out var entry)) return;
        anchor.SizeChanged -= entry.GeometryChanged;
        anchor.ParentChanged -= entry.GeometryChanged;
        anchor.Disposed -= entry.Closed;
        DropSurface(entry);
    }
}
