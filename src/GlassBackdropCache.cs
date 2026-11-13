// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LocalMedia;

// Cache only our static translucent tint, never the desktop, children or video.
// Material-bearing ancestors are still painted by GlassCanvas after this slice.
static class GlassBackdropCache
{
    public const int MaximumEntries = 2;
    public const long MaximumBitmapBytes = 32L * 1024 * 1024;
    public const long MaximumBytes = MaximumEntries * MaximumBitmapBytes;
    static readonly Dictionary<Control, Entry> entries = [];
    static long sequence;
    static (bool Light, int Top, int Bottom)? palette;
    public static bool Enabled { get; } = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LOCALMEDIA_DATA"))
        || Environment.GetEnvironmentVariable("LOCALMEDIA_BACKDROP_CACHE") != "0";

    readonly record struct CacheKey(Size Size, int Dpi, bool Light, int Top, int Bottom);
    sealed class Entry
    {
        public required Control Root;
        public required EventHandler Resized;
        public required EventHandler Closed;
        public Bitmap? Image;
        public NativeTintSurface? Native;
        public CacheKey Key;
        public CacheKey? Failed;
        public long LastUse;
    }

    // All callers paint on the owning UI thread. The cache never accesses HWNDs
    // or changes their styles, and it is not a WinForms double-buffer surface.
    public static bool TryPaint(Control root, Graphics graphics, Point offset, Color top, Color bottom)
    {
        if (!Enabled || root.IsDisposed || !root.IsHandleCreated) return false;
        Size size = root.ClientSize;
        long bytes = (long)size.Width * size.Height * 4;
        if (size.Width <= 0 || size.Height <= 0 || bytes > MaximumBitmapBytes)
        {
            Invalidate(root);
            return false;
        }
        var nextPalette = (UiTheme.IsLight, top.ToArgb(), bottom.ToArgb());
        if (palette != nextPalette)
        {
            foreach (var existing in entries.Values) DropImage(existing);
            palette = nextPalette;
        }
        if (!entries.TryGetValue(root, out var entry))
        {
            if (entries.Count >= MaximumEntries) Remove(entries.Values.MinBy(value => value.LastUse)!.Root);
            entry = new Entry
            {
                Root = root,
                Resized = (_, _) => Invalidate(root),
                Closed = (_, _) => Remove(root)
            };
            entries.Add(root, entry);
            root.SizeChanged += entry.Resized;
            root.Disposed += entry.Closed;
        }
        entry.LastUse = ++sequence;
        var key = new CacheKey(size, root.DeviceDpi, UiTheme.IsLight, top.ToArgb(), bottom.ToArgb());
        if (entry.Image == null || entry.Key != key)
        {
            if (entry.Failed == key) return false;
            DropImage(entry);
            Bitmap? image = null;
            NativeTintSurface? native = null;
            try
            {
                using var measurement = UiPerformance.Measure("backdrop_cache_build", count: (int)bytes);
                // Premultiplied 32-bit alpha preserves the existing SourceCopy
                // contract with the extended DWM frame during the unscaled blit.
                native = NativeTintSurface.TryCreate(size);
                image = native?.Image ?? new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
                image.SetResolution(Math.Max(1, root.DeviceDpi), Math.Max(1, root.DeviceDpi));
                using (var paint = Graphics.FromImage(image))
                {
                    paint.CompositingMode = CompositingMode.SourceCopy;
                    FillTint(paint, new RectangleF(0, 0, size.Width, size.Height), new Rectangle(0, 0, size.Width, size.Height), top, bottom);
                    // Complete GDI+ writes to the shared DIB before the first
                    // native read. This synchronization occurs only on rebuild.
                    paint.Flush(FlushIntention.Sync);
                }
                entry.Image = image; image = null; entry.Native = native; native = null; entry.Key = key;
            }
            catch (Exception error) when (error is ArgumentException or OutOfMemoryException or ExternalException)
            {
                // An unavailable/oversized cache falls back to the same direct
                // material. Do not retry allocation for every child this frame.
                entry.Failed = key;
                UiPerformance.Record("backdrop_cache_failure", 0, error.GetType().Name);
                return false;
            }
            finally { if (native != null) native.Dispose(); else image?.Dispose(); }
        }
        if (entry.Native?.TryPaint(graphics, offset) == true) return true;
        using (UiPerformance.MeasurePaint("paint_backdrop_cached"))
            graphics.DrawImageUnscaled(entry.Image!, -offset.X, -offset.Y);
        return true;
    }

    public static void FillTint(Graphics graphics, RectangleF bounds, Rectangle target, Color top, Color bottom)
    {
        using var tint = new LinearGradientBrush(bounds, Color.Black, Color.Black, LinearGradientMode.Vertical);
        tint.InterpolationColors = new ColorBlend { Positions = [0f, 1f], Colors = [top, bottom] };
        graphics.FillRectangle(tint, target);
    }
    public static void Invalidate(Control root)
    {
        if (entries.TryGetValue(root, out var entry)) DropImage(entry);
    }
    static void DropImage(Entry entry)
    {
        if (entry.Native != null) entry.Native.Dispose(); else entry.Image?.Dispose();
        entry.Image = null; entry.Native = null; entry.Failed = null;
    }
    static void Remove(Control root)
    {
        if (!entries.Remove(root, out var entry)) return;
        root.SizeChanged -= entry.Resized;
        root.Disposed -= entry.Closed;
        DropImage(entry);
    }
}
