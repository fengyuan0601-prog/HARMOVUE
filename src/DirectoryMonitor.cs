// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

namespace LocalMedia;

// Native directory handles belong to one worker. Configure/Dispose only publish
// intent; a sleeping share can never hold a lock needed by the UI thread.
public sealed class DirectoryMonitor : IDisposable
{
    readonly object gate = new();
    readonly Func<string, FileSystemWatcher> create;
    readonly Dictionary<string, FileSystemWatcher> watches = new(StringComparer.OrdinalIgnoreCase);
    readonly System.Collections.Concurrent.ConcurrentQueue<string> errors = new();
    string[] wanted = [], active = [];
    int revision;
    bool running;
    volatile bool disposed;
    long changed, retryAt;
    public DirectoryMonitor() : this(path => new FileSystemWatcher(path)) { }
    internal DirectoryMonitor(Func<string, FileSystemWatcher> create) => this.create = create;
    public bool Due => !disposed && Interlocked.Read(ref changed) is long value && value != 0 && DateTime.UtcNow.Ticks - value > TimeSpan.FromSeconds(3).Ticks;
    public bool RetryDue => Interlocked.Read(ref retryAt) is long value && value != 0 && DateTime.UtcNow.Ticks >= value;
    internal bool IsWatching(string path) => !disposed && Volatile.Read(ref active).Contains(path, StringComparer.OrdinalIgnoreCase);
    public void Acknowledge() => Interlocked.Exchange(ref changed, 0);
    public void Configure(IEnumerable<string> roots)
    {
        var paths = roots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        lock (gate)
        {
            if (disposed) return;
            wanted = paths; revision++;
            if (paths.Length == 0) Acknowledge();
            Start();
        }
    }
    void Start() { if (!running) { running = true; _ = Task.Run(Drain); } }
    public void Retry() { lock (gate) { if (!disposed && RetryDue) { revision++; Start(); } } }
    public bool RequestRetry()
    {
        lock (gate)
        {
            if (disposed || running || !RetryDue) return false;
            revision++; Start(); return true;
        }
    }
    bool Stale(int version) { lock (gate) return revision != version || disposed; }
    void Drain()
    {
        while (true)
        {
            string[] paths; int version;
            lock (gate) { paths = wanted; version = revision; }
            while (errors.TryDequeue(out string? failed))
                if (watches.Remove(failed, out var broken)) broken.Dispose();
            foreach (string old in watches.Keys.Except(paths, StringComparer.OrdinalIgnoreCase).ToArray())
                if (watches.Remove(old, out var watch)) watch.Dispose();
            bool failedAny = false, added = false;
            foreach (string path in paths)
            {
                if (Stale(version)) break;
                if (watches.ContainsKey(path)) continue;
                FileSystemWatcher? watch = null;
                try
                {
                    watch = create(path);
                    watch.IncludeSubdirectories = true;
                    watch.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size;
                    watch.Created += Change; watch.Deleted += Change; watch.Changed += Change; watch.Renamed += Change;
                    watch.Error += (_, _) => { if (disposed) return; errors.Enqueue(path); Interlocked.Exchange(ref changed, DateTime.UtcNow.Ticks); Interlocked.Exchange(ref retryAt, DateTime.UtcNow.AddSeconds(15).Ticks); };
                    watch.EnableRaisingEvents = true;
                    if (Stale(version)) { watch.Dispose(); continue; }
                    watches.Add(path, watch); added = true;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
                { watch?.Dispose(); failedAny = true; }
            }
            Volatile.Write(ref active, watches.Keys.ToArray());
            if (added && paths.Length > 0) Interlocked.Exchange(ref changed, DateTime.UtcNow.Ticks); // Catch changes during async setup.
            Interlocked.Exchange(ref retryAt, failedAny || !errors.IsEmpty ? DateTime.UtcNow.AddSeconds(15).Ticks : 0);
            lock (gate)
            {
                if (version != revision) continue;
                running = false;
                return;
            }
        }
    }
    void Change(object sender, FileSystemEventArgs e)
    {
        lock (gate) { if (disposed || sender is FileSystemWatcher watch && !wanted.Contains(watch.Path, StringComparer.OrdinalIgnoreCase)) return; }
        string extension = Path.GetExtension(e.FullPath).ToLowerInvariant();
        string old = e is RenamedEventArgs renamed ? Path.GetExtension(renamed.OldFullPath).ToLowerInvariant() : "";
        if (extension.Length == 0 || Scanner.Extensions.Contains(extension) || Scanner.Extensions.Contains(old) || extension is ".cue" or ".jpg" or ".jpeg" or ".png" || old == ".cue")
            Interlocked.Exchange(ref changed, DateTime.UtcNow.Ticks);
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; wanted = []; revision++; Acknowledge(); Interlocked.Exchange(ref retryAt, 0); Start();
        }
    }
}
