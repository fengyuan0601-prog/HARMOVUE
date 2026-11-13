// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

namespace LocalMedia;

static class Diagnostics
{
    static System.Collections.Concurrent.ConcurrentQueue<string>? startupTrace;
    sealed record PlaybackSample(DateTime Utc, long Qpc, string Phase, string MediaId, long Generation, string Detail);
    static System.Collections.Concurrent.ConcurrentQueue<PlaybackSample>? playbackTrace;
    internal static bool PlaybackTraceEnabled => Volatile.Read(ref playbackTrace) != null;
    public static bool UiTraceEnabled => Environment.GetEnvironmentVariable("LOCALMEDIA_UI_TRACE") == "1";

    internal static void BeginStartupTrace()
    {
        if (!UiTraceEnabled) return;
        startupTrace = new();
        playbackTrace = new();
    }

    internal static void TracePlayback(string phase, string mediaId = "", long generation = 0, string detail = "")
    {
        var pending = Volatile.Read(ref playbackTrace);
        if (pending == null || pending.Count >= 2048) return;
        pending.Enqueue(new(DateTime.UtcNow, System.Diagnostics.Stopwatch.GetTimestamp(), phase, mediaId, generation, detail));
    }

    internal static void FlushPlaybackTrace(string data)
    {
        if (Interlocked.Exchange(ref playbackTrace, null) is not { } pending) return;
        try
        {
            Directory.CreateDirectory(data);
            var record = new { QpcFrequency = System.Diagnostics.Stopwatch.Frequency, Events = pending.ToArray() };
            File.WriteAllText(Path.Combine(data, "playback-startup.json"), System.Text.Json.JsonSerializer.Serialize(record));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void TraceUi(string data, string phase)
    {
        if (!UiTraceEnabled) return;
        string line = DateTime.UtcNow.ToString("o") + " " + phase;
        if (Volatile.Read(ref startupTrace) is { } pending)
        {
            pending.Enqueue(line);
            return;
        }
        WriteUiTrace(data, line + Environment.NewLine);
    }

    internal static void FlushStartupTrace(string data)
    {
        if (Interlocked.Exchange(ref startupTrace, null) is { } pending)
            WriteUiTrace(data, string.Join(Environment.NewLine, pending) + Environment.NewLine);
    }

    static void WriteUiTrace(string data, string text)
    {
        try
        {
            Directory.CreateDirectory(data);
            File.AppendAllText(Path.Combine(data, "ui-startup.log"), text);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void Log(string data, Exception e)
    {
        Directory.CreateDirectory(data);
        string file = Path.Combine(data, "error.log"), old = Path.Combine(data, "error.previous.log");
        if (File.Exists(file) && new FileInfo(file).Length > 1024 * 1024)
            File.Move(file, old, true);
        File.AppendAllText(file, DateTime.UtcNow.ToString("O") + " " + e + Environment.NewLine);
    }
}
