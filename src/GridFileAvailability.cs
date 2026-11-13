// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.Diagnostics;

namespace LocalMedia;

// Paint/format callbacks must never wait on a disconnected disk or share.
// This is presentation-only: Playback still checks the actual file on open.
sealed class GridFileAvailability : IDisposable
{
    const int Capacity = 512, QueueCapacity = 128;
    static readonly SemaphoreSlim diskReaders = new(2, 2);
    readonly Control host;
    readonly Action<int> invalidateRow;
    readonly object gate = new();
    readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    readonly Queue<(string Path, Entry Entry)> pending = new();
    readonly CancellationTokenSource lifetime = new();
    readonly Func<string, bool> probe;
    readonly Func<int, bool> visibleRow;
    bool running, disposed;
    long sequence;
    sealed class Entry
    {
        public bool? Online;
        public long Checked, Used;
        public bool Pending;
        public readonly HashSet<int> Rows = [];
    }
    public GridFileAvailability(DataGridView grid, Func<string, bool>? probe = null, Func<int, bool>? visibleRow = null)
        : this(grid, grid.InvalidateRow, visibleRow ?? (row => IsGridRowVisible(grid, row)), probe) { }

    internal GridFileAvailability(ListBox list)
        : this(list, row => list.Invalidate(list.GetItemRectangle(row)), row => IsListRowVisible(list, row), null) { }

    GridFileAvailability(Control host, Action<int> invalidateRow, Func<int, bool> visibleRow, Func<string, bool>? probe)
    {
        this.host = host;
        this.invalidateRow = invalidateRow;
        this.visibleRow = visibleRow;
        this.probe = probe ?? File.Exists;
        host.Disposed += (_, _) => Dispose();
    }
    static bool IsGridRowVisible(DataGridView grid, int row)
    {
        if (grid.IsDisposed || !grid.IsHandleCreated || !grid.Visible || row < 0 || row >= grid.RowCount) return false;
        int first = grid.FirstDisplayedScrollingRowIndex;
        return first >= 0 && row >= first && row < first + grid.DisplayedRowCount(true);
    }
    static bool IsListRowVisible(ListBox list, int row)
    {
        if (list.IsDisposed || !list.IsHandleCreated || !list.Visible || row < 0 || row >= list.Items.Count) return false;
        return list.GetItemRectangle(row).IntersectsWith(list.ClientRectangle);
    }
    public bool? Get(string path, int row)
    {
        lock (gate)
        {
            if (disposed) return null;
            // Accessibility/value requests can enumerate the whole virtual
            // table. They must not turn into scans, evict the viewport cache,
            // or enqueue thousands of row invalidations.
            if (!visibleRow(row)) return entries.TryGetValue(path, out var cached) ? cached.Online : null;
            if (!entries.TryGetValue(path, out var entry))
            {
                if (entries.Count >= Capacity)
                {
                    var oldest = entries.Where(p => !p.Value.Pending).MinBy(p => p.Value.Used);
                    if (oldest.Value == null) return null;
                    entries.Remove(oldest.Key);
                }
                entries[path] = entry = new();
            }
            entry.Used = ++sequence;
            bool expired = entry.Checked == 0 || Stopwatch.GetElapsedTime(entry.Checked).TotalSeconds >= 5;
            if (expired && pending.Count < QueueCapacity && !entry.Pending)
            {
                entry.Pending = true; pending.Enqueue((path, entry));
                if (!running) { running = true; _ = Task.Run(Read); }
            }
            if (entry.Pending && entry.Rows.Count < 128) entry.Rows.Add(row);
            return entry.Online;
        }
    }
    async Task Read()
    {
        try
        {
            await diskReaders.WaitAsync(lifetime.Token).ConfigureAwait(false);
            try
            {
                while (true)
                {
                    var results = new List<(Entry Entry, bool Online)>();
                    for (int i = 0; i < 16; i++)
                    {
                        (string Path, Entry Entry) request;
                        lock (gate)
                        {
                            if (disposed || pending.Count == 0) break;
                            request = pending.Dequeue();
                        }
                        using var measurement = UiPerformance.Measure("grid_file_probe");
                        results.Add((request.Entry, probe(request.Path)));
                    }
                    if (results.Count == 0) break;
                    var rows = new HashSet<int>();
                    lock (gate)
                    {
                        if (disposed) return;
                        foreach (var result in results)
                        {
                            var entry = result.Entry;
                            bool changed = entry.Online != result.Online;
                            entry.Online = result.Online; entry.Checked = Stopwatch.GetTimestamp(); entry.Pending = false;
                            if (changed) rows.UnionWith(entry.Rows);
                            entry.Rows.Clear();
                        }
                    }
                    if (rows.Count > 0 && !host.IsDisposed && host.IsHandleCreated)
                        try { host.BeginInvoke((Action)(() => { if (!host.IsDisposed) foreach (int row in rows) if (visibleRow(row)) invalidateRow(row); })); }
                        catch (InvalidOperationException) { }
                }
            }
            finally { diskReaders.Release(); }
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (gate)
            {
                running = false;
                if (disposed) lifetime.Dispose();
                // Get may have queued a request after the reader saw empty.
                if (!disposed && pending.Count > 0) { running = true; _ = Task.Run(Read); }
            }
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; lifetime.Cancel(); pending.Clear(); entries.Clear();
            if (!running) lifetime.Dispose();
            // A native filesystem read cannot be cancelled. Its reader owns
            // the token source until it returns; never wait on it on the UI.
        }
    }
}
