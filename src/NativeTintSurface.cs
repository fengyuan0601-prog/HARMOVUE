// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LocalMedia;

// One top-down DIB backs
// both the GDI+ fallback Bitmap and the source DC: no second pixel allocation.
sealed class NativeTintSurface : IDisposable
{
    public static bool Enabled { get; } = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LOCALMEDIA_DATA"))
        || Environment.GetEnvironmentVariable("LOCALMEDIA_NATIVE_TINT") != "0";
    nint dc, bitmap, previousBitmap;
    bool unavailable;
    public Bitmap Image { get; private set; } = null!;
    NativeTintSurface() { }

    public static NativeTintSurface? TryCreate(Size size)
    {
        if (!Enabled) return null;
        var surface = new NativeTintSurface();
        try
        {
            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = size.Width, Height = -size.Height,
                    Planes = 1, BitCount = 32, Compression = 0, SizeImage = checked((uint)((long)size.Width * size.Height * 4))
                }
            };
            surface.bitmap = CreateDIBSection(0, ref info, 0, out nint pixels, 0, 0);
            if (surface.bitmap == 0 || pixels == 0) throw new ExternalException("CreateDIBSection failed");
            surface.dc = CreateCompatibleDC(0);
            if (surface.dc == 0) throw new ExternalException("CreateCompatibleDC failed");
            surface.previousBitmap = SelectObject(surface.dc, surface.bitmap);
            if (surface.previousBitmap == 0 || surface.previousBitmap == -1) throw new ExternalException("SelectObject failed");
            surface.Image = new Bitmap(size.Width, size.Height, checked(size.Width * 4), PixelFormat.Format32bppPArgb, pixels);
            return surface;
        }
        catch (Exception error) when (error is ArgumentException or OutOfMemoryException or ExternalException or OverflowException)
        {
            UiPerformance.Record("native_tint_create_failure", 0, error.GetType().Name);
            surface.Dispose(); return null;
        }
    }

    public bool TryPaint(Graphics graphics, Point rootOffset)
    {
        using var measurement = UiPerformance.MeasurePaint("paint_native_attempt");
        if (unavailable || dc == 0) return Reject("unavailable");
        nint destination = 0; int saved = 0;
        try
        {
            // GetHdc does not apply GDI+'s transform or clip. Resolve them before
            // borrowing the DC; never call a Graphics method while it is lent.
            if (!TryGeometry(graphics, rootOffset, Image.Size, out var target, out var source)) return Reject("geometry");
            if (target.IsEmpty) return true;
            destination = graphics.GetHdc();
            // Device-pixel coordinates below assume a conventional 32-bit,
            // unmirrored MM_TEXT paint DC. Anything else uses GDI+ unchanged.
            if (GetMapMode(destination) != 1 || GetLayout(destination) != 0
                || GetDeviceCaps(destination, 12) * GetDeviceCaps(destination, 14) != 32
                || !GetViewportOrgEx(destination, out var viewport) || viewport.X != 0 || viewport.Y != 0
                || !GetWindowOrgEx(destination, out var window) || window.X != 0 || window.Y != 0)
                return Reject("dc");
            int mode = GetGraphicsMode(destination);
            if (mode != 1 && (mode != 2 || !GetWorldTransform(destination, out var transform) || !transform.IsIdentity)) return Reject("dc");
            saved = SaveDC(destination);
            if (saved == 0) return Reject("save");
            // Intersect, never replace: the WM_PAINT/native window clip can
            // contain occluded siblings or a complex window shape of its own.
            int clip = IntersectClipRect(destination, target.Left, target.Top, target.Right, target.Bottom);
            if (clip == 0) return Reject("clip");
            if (clip == 1) return true;
            bool copied;
            using (UiPerformance.MeasurePaint("paint_backdrop_native"))
                copied = BitBlt(destination, target.X, target.Y, target.Width, target.Height, dc, source.X, source.Y, 0x00CC0020);
            if (!copied) { unavailable = true; return Reject("copy"); }
            return true;
        }
        catch (Exception error) when (error is ArgumentException or ExternalException or InvalidOperationException)
        {
            unavailable = true;
            UiPerformance.Record("native_tint_paint_failure", 0, error.GetType().Name);
            return false;
        }
        finally
        {
            if (saved != 0) RestoreDC(destination, saved);
            if (destination != 0) graphics.ReleaseHdc(destination);
        }
    }

    static bool TryGeometry(Graphics graphics, Point rootOffset, Size imageSize, out Rectangle target, out Point source)
    {
        target = Rectangle.Empty; source = Point.Empty;
        if (graphics.CompositingMode != CompositingMode.SourceCopy || graphics.PixelOffsetMode is PixelOffsetMode.Half or PixelOffsetMode.HighQuality) return false;
        using var world = graphics.Transform;
        var elements = world.Elements;
        if (elements[0] != 1 || elements[1] != 0 || elements[2] != 0 || elements[3] != 1 || graphics.PageScale != 1) return false;
        // Validate the complete world/page/device mapping, including any DPI
        // scale. Only a one-device-pixel unit and integral translation qualify.
        PointF[] basis = [PointF.Empty, new(1, 0), new(0, 1)];
        graphics.TransformPoints(CoordinateSpace.Device, CoordinateSpace.World, basis);
        if (!Near(basis[1].X - basis[0].X, 1) || !Near(basis[1].Y - basis[0].Y, 0)
            || !Near(basis[2].X - basis[0].X, 0) || !Near(basis[2].Y - basis[0].Y, 1)
            || !Integral(basis[0].X, out _) || !Integral(basis[0].Y, out _)) return false;
        using var region = graphics.Clip;
        using var identity = new Matrix();
        var rectangles = region.GetRegionScans(identity);
        if (rectangles.Length == 0) return true;
        if (rectangles.Length != 1) return false;
        RectangleF clip = rectangles[0];
        PointF[] points = [new(clip.Left, clip.Top), new(clip.Right, clip.Bottom), new(-rootOffset.X, -rootOffset.Y)];
        graphics.TransformPoints(CoordinateSpace.Device, CoordinateSpace.World, points);
        if (!Integral(points[0].X, out int left) || !Integral(points[0].Y, out int top)
            || !Integral(points[1].X, out int right) || !Integral(points[1].Y, out int bottom)
            || !Integral(points[2].X, out int x) || !Integral(points[2].Y, out int y)) return false;
        if (right < left || bottom < top || (long)x + imageSize.Width > int.MaxValue || (long)y + imageSize.Height > int.MaxValue) return false;
        target = Rectangle.Intersect(Rectangle.FromLTRB(left, top, right, bottom), new Rectangle(x, y, imageSize.Width, imageSize.Height));
        if (target.Width <= 0 || target.Height <= 0) { target = Rectangle.Empty; return true; }
        source = new Point(target.X - x, target.Y - y);
        return true;
    }
    static bool Near(float value, float expected) => value == expected;
    static bool Integral(float value, out int integer)
    {
        integer = 0;
        // A conservative bound avoids float/int overflow at huge scroll offsets.
        if (!float.IsFinite(value) || Math.Abs(value) > 16_000_000) return false;
        integer = (int)MathF.Round(value);
        return Near(value, integer);
    }
    static bool Reject(string reason)
    {
        using var count = UiPerformance.MeasurePaint("native_fallback_" + reason);
        return false;
    }
    public void Dispose()
    {
        Image?.Dispose(); Image = null!;
        if (dc != 0 && previousBitmap != 0 && previousBitmap != -1) SelectObject(dc, previousBitmap);
        if (dc != 0) { GdiFlush(); DeleteDC(dc); dc = 0; }
        if (bitmap != 0) { DeleteObject(bitmap); bitmap = 0; }
        previousBitmap = 0;
    }

    [StructLayout(LayoutKind.Sequential)] struct BitmapInfoHeader { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant; }
    [StructLayout(LayoutKind.Sequential)] struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }
    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct XForm
    {
        public float M11, M12, M21, M22, Dx, Dy;
        public readonly bool IsIdentity => Near(M11, 1) && Near(M12, 0) && Near(M21, 0) && Near(M22, 1) && Near(Dx, 0) && Near(Dy, 0);
    }
    [DllImport("gdi32.dll")] static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GdiFlush();
    [DllImport("gdi32.dll")] static extern int SaveDC(nint dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool RestoreDC(nint dc, int saved);
    [DllImport("gdi32.dll")] static extern int GetMapMode(nint dc);
    [DllImport("gdi32.dll")] static extern uint GetLayout(nint dc);
    [DllImport("gdi32.dll")] static extern int GetDeviceCaps(nint dc, int index);
    [DllImport("gdi32.dll")] static extern int GetGraphicsMode(nint dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetWorldTransform(nint dc, out XForm transform);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetViewportOrgEx(nint dc, out NativePoint point);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetWindowOrgEx(nint dc, out NativePoint point);
    [DllImport("gdi32.dll")] static extern int IntersectClipRect(nint dc, int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
}
