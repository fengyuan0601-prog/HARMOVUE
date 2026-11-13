// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace LocalMedia;

internal sealed class StartupWindow : Form
{
    const int DesignWidth = 416, DesignHeight = 224;
    static readonly RectangleF Track = new(158, 155, 100, 2);
    static readonly Color Silver = Color.FromArgb(237, 240, 243);
    readonly System.Windows.Forms.Timer animation = new() { Interval = 32 };
    readonly Button dismiss;
    readonly Font dismissFont = new("Segoe UI", 12f);
    Bitmap? plate;
    long animationStarted;
    bool highContrast;

    public StartupWindow()
    {
        Text = "HARMOVUE";
        AccessibleName = "HARMOVUE 正在启动";
        Icon = Brand.AppIcon();
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(DesignWidth, DesignHeight);
        DoubleBuffered = true;
        highContrast = SystemInformation.HighContrast;
        BackColor = highContrast ? SystemColors.Window : Color.FromArgb(31, 33, 37);
        ForeColor = highContrast ? SystemColors.WindowText : Silver;

        dismiss = new Button
        {
            Text = "×", AccessibleName = "取消启动", Font = dismissFont,
            BackColor = Color.Transparent, ForeColor = ForeColor,
            FlatStyle = FlatStyle.Flat, Margin = Padding.Empty,
            CausesValidation = false, Cursor = Cursors.Hand
        };
        dismiss.FlatAppearance.BorderSize = 0;
        dismiss.FlatAppearance.MouseOverBackColor = highContrast ? SystemColors.Control : Color.FromArgb(55, 59, 65);
        dismiss.FlatAppearance.MouseDownBackColor = highContrast ? SystemColors.ControlDark : Color.FromArgb(66, 70, 76);
        dismiss.Click += (_, _) => Close();
        Controls.Add(dismiss);
        UpdateShape();

        animation.Tick += (_, _) => Invalidate(Rectangle.Inflate(Rectangle.Ceiling(ToClient(Track)), 2, 3));
    }

    protected override void OnLoad(EventArgs e)
    {
        SizeForDpi(DeviceDpi);
        CenterToScreen();
        base.OnLoad(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        SizeForDpi(e.DeviceDpiNew);
    }

    void SizeForDpi(int dpi) => ClientSize = new Size(
        (int)Math.Round(DesignWidth * dpi / 96d), (int)Math.Round(DesignHeight * dpi / 96d));

    protected override void OnShown(EventArgs e)
    {
        if (UiMotion.Enabled) animation.Start();
        base.OnShown(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData != Keys.Escape) return base.ProcessCmdKey(ref msg, keyData);
        Close();
        return true;
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        if (dismiss != null) UpdateShape();
    }

    RectangleF ToClient(RectangleF logical) => new(
        logical.X * ClientSize.Width / DesignWidth, logical.Y * ClientSize.Height / DesignHeight,
        logical.Width * ClientSize.Width / DesignWidth, logical.Height * ClientSize.Height / DesignHeight);

    void UpdateShape()
    {
        plate?.Dispose();
        plate = null;
        if (ClientSize.Width < 2 || ClientSize.Height < 2) return;
        using var outline = UiTheme.Round(ClientRectangle, 25f * ClientSize.Width / DesignWidth);
        Region = new Region(outline);
        dismiss.Bounds = Rectangle.Round(ToClient(new RectangleF(363, 14, 38, 38)));
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (ClientSize.Width == 0 || ClientSize.Height == 0) return;
        plate ??= RenderPlate();
        e.Graphics.DrawImageUnscaled(plate, Point.Empty);
    }

    Bitmap RenderPlate()
    {
        var image = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(image);
        graphics.ScaleTransform(ClientSize.Width / (float)DesignWidth, ClientSize.Height / (float)DesignHeight);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        var bounds = new RectangleF(0, 0, DesignWidth, DesignHeight);
        using var rim = UiTheme.Round(RectangleF.Inflate(bounds, -.75f, -.75f), 24f);
        if (highContrast)
        {
            graphics.Clear(SystemColors.Window);
            using var border = new Pen(SystemColors.WindowText, 1.5f);
            graphics.DrawPath(border, rim);
            using var font = new Font("Segoe UI Semibold", 21, GraphicsUnit.Pixel);
            using var text = new SolidBrush(SystemColors.WindowText);
            using var align = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString("HARMOVUE", font, text, new RectangleF(0, 76, DesignWidth, 48), align);
        }
        else
        {
            using var body = new LinearGradientBrush(bounds, Color.FromArgb(43, 46, 51), Color.FromArgb(21, 23, 26), 105f);
            graphics.FillRectangle(body, bounds);
            using var reflectionShape = new GraphicsPath();
            reflectionShape.AddEllipse(-145, -160, 560, 336);
            using var reflection = new PathGradientBrush(reflectionShape)
            {
                CenterColor = Color.FromArgb(48, Silver), SurroundColors = [Color.FromArgb(0, Silver)]
            };
            graphics.FillRectangle(reflection, bounds);

            using var edge = new LinearGradientBrush(bounds, Color.FromArgb(194, 201, 208), Color.FromArgb(67, 71, 77), 112f);
            using var outer = new Pen(edge, 1.25f);
            graphics.DrawPath(outer, rim);
            using var innerRim = UiTheme.Round(RectangleF.Inflate(bounds, -3, -3), 22f);
            using var inner = new Pen(Color.FromArgb(29, Silver));
            graphics.DrawPath(inner, innerRim);

            using var logo = Brand.Logo();
            // The approved artwork contains the separate app mark to the left of the wordmark.
            var wordmark = new RectangleF(logo.Width * .261f, logo.Height * .298f, logo.Width * .724f, logo.Height * .407f);
            graphics.DrawImage(logo, new RectangleF(114, 91, 188, 23), wordmark, GraphicsUnit.Pixel);
        }

        using var track = new SolidBrush(highContrast ? SystemColors.WindowText : Color.FromArgb(58, Silver));
        graphics.FillRectangle(track, Track);
        return image;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (animation.Enabled)
        {
            if (animationStarted == 0) animationStarted = Stopwatch.GetTimestamp();
            float phase = (float)(Stopwatch.GetElapsedTime(animationStarted).TotalMilliseconds % 1350) / 1350;
            float position = Track.Left - 34 + phase * (Track.Width + 68);
            var beam = new RectangleF(position, Track.Top, 34, Track.Height);
            var state = e.Graphics.Save();
            e.Graphics.ScaleTransform(ClientSize.Width / (float)DesignWidth, ClientSize.Height / (float)DesignHeight);
            e.Graphics.SetClip(Track, CombineMode.Intersect);
            using var light = new LinearGradientBrush(beam, Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal);
            light.InterpolationColors = new ColorBlend
            {
                Positions = [0f, .54f, 1f],
                Colors = [Color.FromArgb(0, Silver), Silver, Color.FromArgb(0, Silver)]
            };
            e.Graphics.FillRectangle(light, beam);
            e.Graphics.Restore(state);
        }
        base.OnPaint(e);
    }

    internal void Finish()
    {
        animation.Stop();
        Hide();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            animation.Dispose();
            plate?.Dispose();
            dismissFont.Dispose();
            Icon?.Dispose();
        }
        base.Dispose(disposing);
    }

    // Keep startup painting independent from the background dependency preparation.
    internal static void Run(string data, string[] args, SingleInstanceInbox inbox)
    {
        using var warmup = NativeWarmup.Begin();
        Diagnostics.TraceUi(data, "startup form begin");
        using var splash = new StartupWindow();
        Diagnostics.TraceUi(data, "startup form constructed");
        void TraceFirstPaint(Form form, string phase)
        {
            if (!Diagnostics.UiTraceEnabled) return;
            void Trace(object? sender, PaintEventArgs e)
            {
                form.Paint -= Trace;
                Diagnostics.TraceUi(data, phase);
                if (phase == "main first paint") Diagnostics.FlushStartupTrace(data);
            }
            form.Paint += Trace;
        }
        TraceFirstPaint(splash, "startup first paint");
        MainWindow? window = null;
        bool ready = false, closing = false;
        void ReleasePrepared(Store? value)
        {
            try { value?.Dispose(); }
            catch (Exception error) { try { Diagnostics.Log(data, error); } catch { } }
        }
        splash.FormClosing += (_, _) => { closing = true; warmup.Dispose(); };
        splash.Shown += async (_, _) =>
        {
            Store? prepared = null;
            try
            {
                Diagnostics.TraceUi(data, splash.animation.Enabled ? "startup motion enabled" : "startup motion disabled");
                splash.Update();
                prepared = await Task.Run(() =>
                {
                    Diagnostics.TraceUi(data, "store prepare begin");
                    var store = new Store(data);
                    Diagnostics.TraceUi(data, "store prepare end");
                    return store;
                });
                Diagnostics.TraceUi(data, "startup preparation ready");
                if (closing || splash.IsDisposed) { ReleasePrepared(prepared); prepared = null; return; }
                UiMenus.Initialize();
                Diagnostics.TraceUi(data, "menus ready");
                window = new MainWindow(data, args.Where(a => a == "--test-audit-window" || a.StartsWith("--test-window-size=", StringComparison.Ordinal)).ToArray(), prepared, deferPlayback: true, nativeWarmup: warmup, startupVideoIntent: D3D11Warmup.FirstExplicitVideoIntent(args));
                prepared = null;
                bool draining = false;
                async void Drain()
                {
                    if (draining || window.IsDisposed || window.Disposing) return;
                    draining = true;
                    try
                    {
                        while (!window.IsDisposed && inbox.TryRead(out var paths))
                        {
                            window.Show();
                            if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
                            window.Activate();
                            try { await window.OpenPaths(paths); } catch (Exception e) { Diagnostics.Log(data, e); }
                        }
                    }
                    finally { draining = false; }
                }
                window.FormClosed += (_, _) => { ready = true; splash.Close(); };
                TraceFirstPaint(window, "main first paint");
                window.Show();
                window.Update();
                splash.Finish();
                inbox.Attach(() => window.BeginInvoke((Action)Drain));
            }
            catch (Exception error)
            {
                ReleasePrepared(prepared);
                if (!closing && !splash.IsDisposed)
                {
                    Diagnostics.Log(data, error); Environment.ExitCode = 1;
                    MessageBox.Show(splash, "HARMOVUE 无法启动：" + error.Message, "HARMOVUE"); splash.Close();
                }
            }
        };
        Application.Run(splash);
        warmup.Dispose();
        if (ready) window?.Dispose();
    }
}
