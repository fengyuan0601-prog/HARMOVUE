// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.Collections.Concurrent;

namespace LocalMedia;

// One budget for generated covers, thumbnail images and normalized subtitles.
// The directory must be the application's dedicated cache directory. Media and
// persistent user-selected artwork are never candidates for eviction.
public static class MediaCache
{
    public const long MaximumBytes = 256L * 1024 * 1024;
    public const long WorkspaceBytes = 8L * 1024 * 1024;
    static readonly ConcurrentDictionary<string, CacheState> states = new(StringComparer.OrdinalIgnoreCase);
    sealed class CacheState
    {
        public readonly object Gate = new();
        public readonly Dictionary<string, int> Pins = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, DateTime> Uses = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Workspaces = new(StringComparer.OrdinalIgnoreCase);
    }
    sealed class Lease(CacheState state, string path) : IDisposable
    {
        int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (state.Gate)
            {
                if (state.Pins.TryGetValue(path, out int count))
                { if (count <= 1) state.Pins.Remove(path); else state.Pins[path] = count - 1; }
            }
        }
    }
    sealed class EmptyLease : IDisposable { public void Dispose() { } }
    static readonly IDisposable empty = new EmptyLease();

    static string Root(string directory, bool create = false)
    {
        string full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Path.GetFileName(full).Equals("cache", StringComparison.OrdinalIgnoreCase))
            throw new IOException("缓存操作仅允许播放器的 cache 目录。");
        CheckChain(full);
        if (create) { Directory.CreateDirectory(full); CheckChain(full); }
        return full;
    }
    static void CheckChain(string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("缓存位置包含链接目录，已停止操作以保护原文件。");
    }
    static string Destination(string root, string path)
    {
        string full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("生成缓存必须直接位于播放器缓存目录。");
        CheckChain(full);
        return full;
    }
    internal static string NativeShaderDirectory(string directory)
    {
        string root = Root(directory, create: true);
        string destination = Destination(root, Path.Combine(root, "mpv-shaders"));
        Directory.CreateDirectory(destination);
        CheckChain(destination);
        return destination;
    }
    static IEnumerable<FileInfo> Files(string root)
    {
        if (!Directory.Exists(root)) return [];
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        return Directory.EnumerateFiles(root, "*", options).Select(path => new FileInfo(path)).Where(file => file.Exists).ToArray();
    }
    static bool Working(CacheState state, string path) => state.Workspaces.Any(work => path.StartsWith(work + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    static bool MakeRoom(CacheState state, string root, long additional)
    {
        if (additional < 0 || additional > MaximumBytes) return false;
        var files = Files(root).Where(file => !Working(state, file.FullName)).ToArray();
        long used = files.Sum(file => file.Length) + state.Workspaces.Count * WorkspaceBytes;
        foreach (var file in files.OrderBy(file => state.Uses.GetValueOrDefault(file.FullName, file.LastWriteTimeUtc)))
        {
            if (used + additional <= MaximumBytes) return true;
            if (state.Pins.ContainsKey(file.FullName)) continue;
            try
            {
                CheckChain(file.FullName);
                long size = file.Length; file.Delete(); used -= size; state.Uses.Remove(file.FullName);
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return used + additional <= MaximumBytes;
    }
    static IDisposable Pin(CacheState state, string path)
    {
        state.Pins[path] = state.Pins.GetValueOrDefault(path) + 1;
        state.Uses[path] = DateTime.UtcNow;
        return new Lease(state, path);
    }
    public static IDisposable Acquire(string directory, string path)
    {
        string root = Root(directory), full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase)) return empty;
        var state = states.GetOrAdd(root, _ => new());
        lock (state.Gate)
        {
            Destination(root, full);
            if (!File.Exists(full)) throw new FileNotFoundException("缓存已失效，请重新加载。", full);
            return Pin(state, full);
        }
    }
    public static bool TryWrite(string directory, string destination, byte[] bytes) => Publish(directory, destination, bytes.LongLength, stream => stream.Write(bytes), false, out _);
    public static bool TryWritePinned(string directory, string destination, byte[] bytes, out IDisposable lease) => Publish(directory, destination, bytes.LongLength, stream => stream.Write(bytes), true, out lease);
    public static bool TryCopy(string directory, string destination, string source)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Publish(directory, destination, input.Length, stream => input.CopyTo(stream), false, out _);
    }
    static bool Publish(string directory, string destination, long length, Action<Stream> write, bool pin, out IDisposable lease)
    {
        lease = empty;
        string root = Root(directory, create: true), path = Destination(root, destination);
        var state = states.GetOrAdd(root, _ => new());
        lock (state.Gate)
        {
            if (File.Exists(path))
            {
                // Existing entries may come from an older unbounded cache.
                // Protect this entry while bringing the rest under the limit.
                using var guard = Pin(state, path);
                if (!MakeRoom(state, root, 0)) return false;
                if (pin) lease = Pin(state, path);
                return true;
            }
            if (!MakeRoom(state, root, length)) return false;
            string temporary = Path.Combine(root, ".publish-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { write(stream); if (stream.Length != length) throw new IOException("生成缓存大小发生变化，未发布不完整文件。"); stream.Flush(); }
                CheckChain(path); File.Move(temporary, path, overwrite: false);
                state.Uses[path] = DateTime.UtcNow;
                if (pin) lease = Pin(state, path);
                return true;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    public sealed class Workspace : IDisposable
    {
        readonly CacheState state;
        int disposed;
        public string DirectoryPath { get; }
        internal Workspace(object owner, string path) { state = (CacheState)owner; DirectoryPath = path; }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (state.Gate)
            {
                // Only this generated workspace. No recursive delete through a
                // reparse point, and never a path supplied by a media record.
                try
                {
                    CheckChain(DirectoryPath);
                    foreach (var file in Files(DirectoryPath)) { CheckChain(file.FullName); file.Delete(); }
                    if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath);
                }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
                finally { state.Workspaces.Remove(DirectoryPath); }
                // If cleanup was blocked by a file handle, its real bytes are
                // counted by the next budget pass instead of being forgotten.
            }
        }
    }
    public static Workspace? CreateWorkspace(string directory)
    {
        string root = Root(directory, create: true);
        var state = states.GetOrAdd(root, _ => new());
        lock (state.Gate)
        {
            if (!MakeRoom(state, root, WorkspaceBytes)) return null;
            string path = Path.Combine(root, ".work-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path); state.Workspaces.Add(path);
            return new Workspace(state, path);
        }
    }
    public static Task<long> GetSizeAsync(string directory) => Task.Run(() =>
    {
        string root = Root(directory); var state = states.GetOrAdd(root, _ => new());
        lock (state.Gate) return Files(root).Sum(file => file.Length);
    });
    public static Task ClearAsync(string directory) => Task.Run(() =>
    {
        string root = Root(directory); var state = states.GetOrAdd(root, _ => new());
        lock (state.Gate)
            foreach (var file in Files(root))
            {
                if (state.Pins.ContainsKey(file.FullName) || Working(state, file.FullName)) continue;
                try { CheckChain(file.FullName); file.Delete(); state.Uses.Remove(file.FullName); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
    });
}
