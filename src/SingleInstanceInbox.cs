// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.IO.Pipes;
using System.Text.Json;

namespace LocalMedia;

// Open the first listener before constructing the database/player/window.
// Explorer requests arriving during a slow startup wait here until Shown.
public sealed class SingleInstanceInbox : IDisposable
{
    readonly CancellationTokenSource cancel = new();
    readonly Queue<string[]> pending = new();
    readonly object gate = new();
    readonly Task receiver;
    Action? notify;
    bool disposed;

    public SingleInstanceInbox(string name) : this(name, []) { }
    public SingleInstanceInbox(string name, string[] initial)
    {
        if (initial.Length > 0) pending.Enqueue(initial);
        var first = Create(name); // Synchronous: accepting a client is now safe.
        receiver = Receive(name, first);
    }
    static NamedPipeServerStream Create(string name) => new(name, PipeDirection.In, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    async Task Receive(string name, NamedPipeServerStream server)
    {
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                await using (server)
                {
                    await server.WaitForConnectionAsync(cancel.Token).ConfigureAwait(false);
                    using var reader = new StreamReader(server);
                    string? line = await reader.ReadLineAsync(cancel.Token).ConfigureAwait(false);
                    if (line != null)
                    {
                        Enqueue(JsonSerializer.Deserialize<string[]>(line) ?? []);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { }
            catch (JsonException) { }
            if (cancel.IsCancellationRequested) break;
            server = Create(name);
        }
    }
    static void Notify(Action? callback)
    {
        try { callback?.Invoke(); }
        catch (InvalidOperationException) { /* The UI may be closing. */ }
    }
    public void Enqueue(string[] paths)
    {
        Action? callback;
        lock (gate) { if (disposed) return; pending.Enqueue(paths); callback = notify; }
        Notify(callback);
    }
    public void Attach(Action callback)
    {
        lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); notify = callback; }
        Notify(callback);
    }
    public bool TryRead(out string[] paths)
    {
        lock (gate) return pending.TryDequeue(out paths!);
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; notify = null; }
        cancel.Cancel();
        receiver.GetAwaiter().GetResult();
        cancel.Dispose();
    }
}
