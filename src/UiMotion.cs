// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LocalMedia;

// UI-thread motion uses elapsed time, so a delayed frame never queues a long
// backlog. No periodic work remains after settling; OS reduced motion wins.
sealed class UiMotion(Control owner, string name) : IDisposable
{
    readonly System.Windows.Forms.Timer timer = new() { Interval = 15 };
    Action<double>? update;
    Action? complete;
    EventHandler? pendingStart;
    long started, previous;
    int duration;
    bool wired;
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetSystemParameter(uint action, uint parameter, out int value, uint flags);
    public static bool Enabled => !SystemInformation.HighContrast && GetSystemParameter(0x1042, 0, out int enabled, 0) && enabled != 0;
    public void Run(int milliseconds, Action<double> frame, Action? finished = null)
    {
        Stop();
        if (!Enabled || !owner.Visible || !owner.IsHandleCreated || owner.IsDisposed)
        { UiPerformance.Record(name + "_snap", 0); frame(1); finished?.Invoke(); return; }
        if (!wired) { timer.Tick += Tick; wired = true; }
        update = frame; complete = finished; duration = Math.Max(1, milliseconds);
        frame(0);
        // Render queues its final layout/paint before application idle. Starting
        // the clock here would consume the whole transition in that first paint.
        pendingStart = (_, _) =>
        {
            if (pendingStart != null) Application.Idle -= pendingStart;
            pendingStart = null;
            if (owner.IsDisposed) { Stop(); return; }
            if (!Enabled || !owner.Visible)
            { update?.Invoke(1); var finished = complete; Stop(); finished?.Invoke(); return; }
            started = previous = Stopwatch.GetTimestamp();
            UiPerformance.Record(name + "_start", 0, count: duration);
            timer.Start();
        };
        Application.Idle += pendingStart;
    }
    void Tick(object? sender, EventArgs e)
    {
        if (owner.IsDisposed) { Stop(); return; }
        long now = Stopwatch.GetTimestamp();
        UiPerformance.Record(name + "_frame_gap", Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds);
        previous = now;
        double t = !owner.Visible || !Enabled ? 1 : Math.Min(1, Stopwatch.GetElapsedTime(started, now).TotalMilliseconds / duration);
        using (UiPerformance.Measure(name + "_frame_work")) update?.Invoke(1 - Math.Pow(1 - t, 3));
        if (t >= 1) { UiPerformance.Record(name + "_complete", Stopwatch.GetElapsedTime(started).TotalMilliseconds); var finished = complete; Stop(); finished?.Invoke(); }
    }
    public void Stop()
    {
        timer.Stop();
        if (pendingStart != null) Application.Idle -= pendingStart;
        pendingStart = null; update = null; complete = null;
    }
    public void Dispose() { Stop(); timer.Dispose(); }
}
