// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LocalMedia;

// Opt-in measurements of actual user-driven UI work. Nothing is written in the
// normal player; samples are buffered and flushed once when its window closes.
sealed class UiPerformance : IDisposable
{
    static volatile UiPerformance? current;
    readonly object gate = new();
    readonly List<Sample> samples = [];
    readonly Dictionary<(string Name, long Bucket), PaintAggregate> paintBuckets = [];
    readonly Dictionary<string, PaintAggregate> paintTotals = [];
    readonly HashSet<EventHandler> idleHandlers = [];
    readonly string directory;
    readonly long started = Stopwatch.GetTimestamp();
    readonly DateTime startedUtc = DateTime.UtcNow;
    long previousTick;
    int dropped;
    int droppedPaintBuckets;
    bool disposed;
    const int Limit = 30000;
    const int PaintBucketLimit = 4096;
    const int PaintBucketMilliseconds = 250;
    public bool Enabled { get; }

    public UiPerformance(string directory)
    {
        this.directory = directory;
        Enabled = Environment.GetEnvironmentVariable("LOCALMEDIA_PERF_TRACE") == "1"
            && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LOCALMEDIA_DATA"));
        if (Enabled) current = this;
    }
    public static long Timestamp => current is null ? 0 : Stopwatch.GetTimestamp();
    public static Scope Measure(string name, string? detail = null, int count = 0) => new(current, name, detail, count);
    public static PaintScope MeasurePaint(string name) => new(current, name);
    public static void Record(string name, double elapsedMs, string? detail = null, int count = 0) => current?.Add(name, elapsedMs, detail, count);
    void Add(string name, double elapsedMs, string? detail, int count)
    {
        lock (gate)
        {
            if (disposed) return;
            if (samples.Count >= Limit) { dropped++; return; }
            samples.Add(new Sample(name, elapsedMs, Stopwatch.GetElapsedTime(started).TotalMilliseconds, detail, count, Environment.CurrentManagedThreadId));
        }
    }
    void AddPaint(string name, double elapsedMs)
    {
        double atMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long bucket = (long)(atMs / PaintBucketMilliseconds);
        lock (gate)
        {
            if (disposed) return;
            if (!paintTotals.TryGetValue(name, out var total))
                paintTotals[name] = total = new PaintAggregate { Name = name };
            total.Add(elapsedMs);
            if (!paintBuckets.TryGetValue((name, bucket), out var interval))
            {
                if (paintBuckets.Count >= PaintBucketLimit) { droppedPaintBuckets++; return; }
                paintBuckets[(name, bucket)] = interval = new PaintAggregate { Name = name, AtMs = bucket * PaintBucketMilliseconds };
            }
            interval.Add(elapsedMs);
        }
    }
    public void Tick()
    {
        if (!Enabled) return;
        long now = Stopwatch.GetTimestamp();
        if (previousTick != 0) Add("timer_gap", Stopwatch.GetElapsedTime(previousTick, now).TotalMilliseconds, "200 ms WinForms timer", 0);
        previousTick = now;
    }
    public void RenderDispatch(Control host, long renderStarted, string page)
    {
        if (!Enabled || renderStarted == 0 || host.IsDisposed || !host.IsHandleCreated) return;
        try
        {
            host.BeginInvoke((Action)(() =>
            {
                if (disposed || host.IsDisposed) return;
                Add("render_dispatch", Stopwatch.GetElapsedTime(renderStarted).TotalMilliseconds, page, 0);
                EventHandler? idle = null;
                idle = (_, _) =>
                {
                    Application.Idle -= idle;
                    if (idle != null) idleHandlers.Remove(idle);
                    if (!disposed) Add("render_idle", Stopwatch.GetElapsedTime(renderStarted).TotalMilliseconds, page, 0);
                };
                idleHandlers.Add(idle); Application.Idle += idle;
            }));
        }
        catch (InvalidOperationException) when (host.IsDisposed || !host.IsHandleCreated) { }
    }
    public void Dispose()
    {
        if (!Enabled || disposed) return;
        foreach (var handler in idleHandlers) Application.Idle -= handler;
        idleHandlers.Clear();
        Sample[] completed;
        PaintAggregate[] completedPaint, totalPaint;
        lock (gate)
        {
            disposed = true; completed = samples.ToArray();
            completedPaint = paintBuckets.Values.OrderBy(p => p.AtMs).ThenBy(p => p.Name).ToArray();
            totalPaint = paintTotals.Values.OrderBy(p => p.Name).ToArray();
        }
        if (ReferenceEquals(current, this)) current = null;
        var summary = completed.GroupBy(s => s.Name).ToDictionary(g => g.Key, g =>
        {
            double[] times = g.Select(s => s.ElapsedMs).Order().ToArray();
            double Percentile(double p) => times[Math.Clamp((int)Math.Ceiling(times.Length * p) - 1, 0, times.Length - 1)];
            return new { Count = times.Length, MeanMs = times.Average(), P50Ms = Percentile(.5), P95Ms = Percentile(.95), MaxMs = times[^1] };
        });
        try
        {
            Directory.CreateDirectory(directory);
            string filename = "ui-performance-" + startedUtc.ToString("yyyyMMdd-HHmmss-fff") + ".json";
            File.WriteAllText(Path.Combine(directory, filename), JsonSerializer.Serialize(new
            {
                StartedUTC = startedUtc,
                ProcessId = Environment.ProcessId,
                Assembly = typeof(UiPerformance).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                RenderBatchEnabled = UiRedrawBatch.Enabled,
                BackdropCache = new { GlassBackdropCache.Enabled, NativeTintEnabled = NativeTintSurface.Enabled, GlassBackdropCache.MaximumEntries, GlassBackdropCache.MaximumBitmapBytes, GlassBackdropCache.MaximumBytes },
                MaterialCache = new { MaterialSurfaceCache.Enabled, MaterialSurfaceCache.MaximumEntries, MaterialSurfaceCache.MaximumBitmapBytes, MaterialSurfaceCache.MaximumBytes, MaterialSurfaceCache.MaximumCombinedTintBytes },
                Definitions = new
                {
                    render_sync = "Elapsed synchronous Render work; excludes queued final arrangement and paint.",
                    render_dispatch = "Render entry to a posted UI callback after queued arrangement; not pixel presentation or input latency.",
                    render_idle = "Render entry until the next application idle after that callback; includes UI queue/paint work, not compositor display time.",
                    timer_gap = "Interval between actual 200 ms WinForms timer callbacks; a dispatcher responsiveness indicator, not input-to-photon latency.",
                    paint = "Inclusive elapsed time and call counts in fixed paint scopes. Scopes overlap/nest; do not sum different scope names. PaintBuckets group calls by completion time in 250 ms intervals. paint_label measures text OnPaint only, with its background counted by paint_parent_slice/paint_surface. paint_backdrop_cached measures the cached tint blit; backdrop_cache_build measures regeneration, and paint_backdrop_uncached measures fallback tint rendering. No control names or media paths are recorded.",
                    material_cache = "Default-on cache of static composed tint/plate pixels only; explicit LOCALMEDIA_DATA plus LOCALMEDIA_MATERIAL_CACHE=0 opts out for comparison. paint_material_layers includes direct and cache-build vector layer drawing. material_cache_build includes that work. paint_material_cache_attempt/copy and paint_native_attempt/backdrop_native nest and must not be summed; native copy scopes also include material-cache copies. material_cache_bytes Count is current allocated material bytes after a build, not a sample count. Fallback counters do not imply a blank frame: they use the original tint/vector path.",
                    other_scopes = "Measured elapsed work in the named method; ThreadId distinguishes UI and worker calls."
                },
                Dropped = dropped, Summary = summary, Samples = completed,
                PaintBucketMilliseconds, DroppedPaintBuckets = droppedPaintBuckets, PaintSummary = totalPaint, PaintBuckets = completedPaint
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    sealed record Sample(string Name, double ElapsedMs, double AtMs, string? Detail, int Count, int ThreadId);
    sealed class PaintAggregate
    {
        public required string Name { get; init; }
        public double AtMs { get; init; }
        public long Count { get; private set; }
        public double TotalMs { get; private set; }
        public double MaxMs { get; private set; }
        public double MeanMs => Count == 0 ? 0 : TotalMs / Count;
        public void Add(double elapsedMs) { Count++; TotalMs += elapsedMs; MaxMs = Math.Max(MaxMs, elapsedMs); }
    }
    public readonly struct PaintScope : IDisposable
    {
        readonly UiPerformance? owner;
        readonly string name;
        readonly long start;
        internal PaintScope(UiPerformance? owner, string name)
        {
            this.owner = owner; this.name = name;
            start = owner is null ? 0 : Stopwatch.GetTimestamp();
        }
        public void Dispose() => owner?.AddPaint(name, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }
    public readonly struct Scope : IDisposable
    {
        readonly UiPerformance? owner;
        readonly string name;
        readonly string? detail;
        readonly int count;
        readonly long start;
        internal Scope(UiPerformance? owner, string name, string? detail, int count)
        {
            this.owner = owner; this.name = name; this.detail = detail; this.count = count;
            start = owner is null ? 0 : Stopwatch.GetTimestamp();
        }
        public void Dispose() => owner?.Add(name, Stopwatch.GetElapsedTime(start).TotalMilliseconds, detail, count);
    }
}

// Render already suspends managed layout while replacing the browse tree.
// Coalesce its final paint after queued Arrange callbacks, without changing
// native visibility. WM_SETREDRAW(false) removes WS_VISIBLE from the parent:
// newly created opaque layered grid/edit/combo children then initialize under
// a hidden native ancestor and may retain black/unpainted redirection surfaces.
// Never freeze/recreate that ancestor, the Form or the libmpv output HWND.
sealed class UiRedrawBatch(Control surface) : IDisposable
{
    static readonly bool isolated = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LOCALMEDIA_DATA"));
    public static bool Enabled { get; } = !isolated || Environment.GetEnvironmentVariable("LOCALMEDIA_RENDER_BATCH") != "0";
    int revision;
    bool disposed;
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool RedrawWindow(nint window, nint updateRectangle, nint updateRegion, uint flags);

    public int Begin() => ++revision;
    public void CompleteAfterLayout(int token)
    {
        if (!Enabled || disposed || surface.IsDisposed || !surface.IsHandleCreated) return;
        try
        {
            // Page builders queue their final DPI-aware Arrange before this.
            // Only the newest tree needs a final paint. Native child HWNDs stay
            // visible throughout construction, preserving their layered/GDI
            // alpha protection; there is no delayed visibility restoration.
            surface.BeginInvoke((Action)(() => { if (token == revision) PaintFinalTree(); }));
        }
        catch (InvalidOperationException) { /* Owner is closing; nothing was frozen. */ }
    }
    void PaintFinalTree()
    {
        if (disposed || surface.IsDisposed || !surface.IsHandleCreated || !surface.Visible) return;
        using var measure = UiPerformance.Measure("browse_redraw_complete");
        // Include native child/nonclient areas and present once after layout.
        // RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW.
        RedrawWindow(surface.Handle, 0, 0, 0x0001 | 0x0004 | 0x0400 | 0x0080 | 0x0100);
    }
    public void Dispose() { disposed = true; revision++; }
}
