// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.Diagnostics;
using System.Globalization;

namespace LocalMedia;

public sealed partial class Playback
{
    // Scanner-owned Media fields can change in place. A native occurrence must
    // keep the exact source and segment bounds that were sent to loadfile.
    sealed record QueueSource(Media Media, string Path, string Cue, string DeclaredPath, double Start, double? End, string Codepage, double Duration)
    {
        public static QueueSource From(Media media) => new(media, media.Path, media.Cue, media.CueDeclaredPath.Length == 0 ? media.Path : media.CueDeclaredPath, media.Start, media.End, media.SubCodepage, media.Duration);
    }
    readonly List<QueueSource> nativeQueue = [];
    readonly List<int> nativeOrder = [];
    readonly Dictionary<int, long> logicalEntries = [];
    readonly Dictionary<long, string> nativeAliases = [];
    QueueSource? currentSource;
    long? currentEntry;
    double currentPosition;
    int fillCursor;
    bool endTimerHeld;
    int? stagedEndedIndex;

    void ResetNativeQueue(IEnumerable<Media> queue)
    {
        nativeQueue.Clear(); nativeQueue.AddRange(queue.Select(QueueSource.From));
        nativeOrder.Clear(); logicalEntries.Clear(); nativeOccurrences.Clear();
        nativeAliases.Clear(); stagedEndedIndex = null;
        currentSource = null; currentEntry = null; currentPosition = 0; fillCursor = 0;
    }

    void AddNative(int logical, double offset = 0, bool startIfEmpty = true)
    {
        if (logicalEntries.ContainsKey(logical)) return;
        int insertion = nativeOrder.BinarySearch(logical);
        if (insertion < 0) insertion = ~insertion;
        string action = nativeOrder.Count == 0 ? (startIfEmpty ? "replace" : "append")
            : insertion == nativeOrder.Count ? "append" : "insert-at";
        string alias = LoadNative(nativeQueue[logical], offset, action, action == "insert-at" ? insertion : -1);
        if (!long.TryParse(Engine.Get($"playlist/{insertion}/id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
            throw new InvalidOperationException("播放内核未返回队列条目标识。");
        nativeOrder.Insert(insertion, logical);
        nativeOccurrences[id] = logical;
        logicalEntries[logical] = id;
        if (nativeQueue[logical].Cue.Length != 0) nativeAliases[id] = alias;
    }

    void SetCurrentOccurrence(int logical)
    {
        var nextSource = logical >= 0 && logical < nativeQueue.Count ? nativeQueue[logical] : null;
        if (!ReferenceEquals(currentSource, nextSource)) currentPosition = 0;
        currentSource = nextSource;
        currentEntry = logicalEntries.TryGetValue(logical, out long id) ? id : null;
    }

    void EnsureSuccessors(int logical)
    {
        // Keep multiple real successors ready, including when a user jumps to
        // an entry whose surrounding queue has not yet been staged.
        for (int i = logical + 1; i < Math.Min(nativeQueue.Count, logical + 9); i++) AddNative(i);
        if (store.State.Repeat == "all" && logical + 9 >= nativeQueue.Count)
            for (int i = 0; i < Math.Min(nativeQueue.Count, 8); i++) AddNative(i);
    }

    void PumpNativeQueue()
    {
        if (loading || nativeQueue.Count == 0 || fillCursor >= nativeQueue.Count) return;
        long started = Stopwatch.GetTimestamp();
        do { AddNative(fillCursor++); }
        while (fillCursor < nativeQueue.Count && Stopwatch.GetElapsedTime(started).TotalMilliseconds < 3);
        if (nativeOrder.Count == nativeQueue.Count) ApplyRepeatPolicy();
    }

    public void SelectQueueIndex(int logical)
    {
        if (logical < 0 || logical >= nativeQueue.Count) throw new ArgumentOutOfRangeException(nameof(logical));
        CancelPendingSeek();
        RestoreAfterEndTimer();
        bool paused = Engine.Get("pause") == "yes";
        Engine.Set("pause", "yes");
        try
        {
            AddNative(logical); EnsureSuccessors(logical);
            PruneAfterForwardBlock(logical);
            ApplyRepeatPolicy();
            Engine.Set("playlist-pos", nativeOrder.BinarySearch(logical));
        }
        finally { Engine.Set("pause", paused ? "yes" : "no"); }
    }

    void PruneAfterForwardBlock(int logical)
    {
        int end = nativeOrder.BinarySearch(logical);
        while (end + 1 < nativeOrder.Count && nativeOrder[end + 1] == nativeOrder[end] + 1) end++;
        // Only the contiguous forward block may follow this selection. A far
        // block retained from an old jump would otherwise skip an unstaged gap
        // before the UI timer gets a chance to observe END_FILE.
        for (int i = nativeOrder.Count - 1; i > end; i--)
        {
            int removed = nativeOrder[i]; long id = logicalEntries[removed];
            Engine.Command("playlist-remove", i.ToString(CultureInfo.InvariantCulture));
            nativeOrder.RemoveAt(i); logicalEntries.Remove(removed); nativeOccurrences.Remove(id);
            if (nativeAliases.Remove(id, out string? alias)) cueGuard?.Remove(alias);
            fillCursor = Math.Min(fillCursor, removed);
        }
    }

    void ContinueStagedQueue()
    {
        if (!Engine.PollDrained || !stagedEndedIndex.HasValue) return;
        if (Engine.Get("idle-active") != "yes") { stagedEndedIndex = null; return; }
        int ended = stagedEndedIndex.Value; stagedEndedIndex = null;
        if (Current == null || StopAtEnd || endTimerHeld || Current.Kind == "video" && !store.State.AutoNext) return;
        int next = ended + 1;
        if (next >= nativeQueue.Count)
        {
            if (store.State.Repeat != "all" || nativeQueue.Count == 0) return;
            next = 0;
        }
        // This is a native window exhaustion, not user activation. The saved
        // managed queue and its last actually-played history stay authoritative.
        SelectQueueIndex(next);
    }

    bool TryNativeEntry(out long id)
    {
        id = 0;
        string position = Engine.Get("playlist-playing-pos");
        return int.TryParse(position, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) && index >= 0
            && long.TryParse(Engine.Get($"playlist/{index}/id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }

    bool TryCurrentPosition(out double position)
    {
        position = 0;
        if (Current == null || loading || currentSource == null || !currentEntry.HasValue
            || !TryNativeEntry(out long before) || before != currentEntry.Value) return false;
        string value = Engine.Get("time-pos");
        if (!TryNativeEntry(out long after) || before != after
            || !double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out double absolute) || !double.IsFinite(absolute)) return false;
        position = Math.Max(0, absolute - currentSource.Start);
        return true;
    }

    bool MatchesCurrentRecord() => Current is { } media && currentSource is { } source
        && media.Path.Equals(source.Path, StringComparison.OrdinalIgnoreCase)
        && media.Cue.Equals(source.Cue, StringComparison.OrdinalIgnoreCase)
        && media.Start == source.Start && media.End == source.End;

    void RestoreAfterEndTimer()
    {
        if (!endTimerHeld) return;
        endTimerHeld = false;
        ApplyEndPolicy();
    }

    void ShiftNativeIndices(int from, int delta)
    {
        for (int i = 0; i < nativeOrder.Count; i++) if (nativeOrder[i] >= from) nativeOrder[i] += delta;
        foreach (long id in nativeOccurrences.Keys.ToArray()) if (nativeOccurrences[id] >= from) nativeOccurrences[id] += delta;
        logicalEntries.Clear();
        foreach (var pair in nativeOccurrences) logicalEntries[pair.Value] = pair.Key;
    }
}
