// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.Runtime.InteropServices;
using System.Globalization;
using System.Text.Json;
using System.Diagnostics;

namespace LocalMedia;

public sealed partial class Mpv : IDisposable
{
    const string Dll = "libmpv-2.dll";
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern nint mpv_create();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_initialize(nint h);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_set_option_string(nint h, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_set_property_string(nint h, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern nint mpv_get_property_string(nint h, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_command(nint h, nint args);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern nint mpv_wait_event(nint h, double timeout);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern void mpv_free(nint p);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern void mpv_terminate_destroy(nint h);
    [StructLayout(LayoutKind.Sequential)]
    public struct Event
    {
        public int Id; public int Error; public ulong User; public nint Data;
    }
    public nint Handle
    {
        get; private set;
    }
    public event Action<int, int>? EventReceived;
    public int EndReason { get; private set; } = -1;
    public long? StartedEntryId { get; private set; }
    public long? EndedEntryId { get; private set; }
    public bool PollDrained { get; private set; }
    public Mpv(nint window, bool silent = false, string? pcm = null, string? thumbnail = null)
        : this(window, silent, pcm, thumbnail, false) { }

    internal Mpv(nint window, bool silent, string? pcm, string? thumbnail, bool tracePreparation, string? shaderCachePath = null)
    {
        // Body time excludes any preparation before entering this constructor.
        // Native-call scopes can include first binding/loading, not just mpv work.
        using var measure = UiPerformance.Measure("mpv_constructor_body");
        if (tracePreparation) Diagnostics.TracePlayback("native create begin");
        using (UiPerformance.Measure("mpv_create_call")) Handle = mpv_create();
        if (tracePreparation) Diagnostics.TracePlayback("native create end");
        if (Handle == 0)
            throw new Exception("无法创建 libmpv");
        try { Initialize(window, silent, pcm, thumbnail, tracePreparation, shaderCachePath); }
        catch { Dispose(); throw; }
    }
    void Initialize(nint window, bool silent, string? pcm, string? thumbnail, bool tracePreparation, string? shaderCachePath)
    {
        using (UiPerformance.Measure("mpv_options"))
        {
            Option("config", "no");
            string? nativeLog = Environment.GetEnvironmentVariable("LOCALMEDIA_MPV_LOG");
            if (tracePreparation && Diagnostics.PlaybackTraceEnabled && nativeLog != null && Path.IsPathFullyQualified(nativeLog)
                && string.Equals(Path.GetPathRoot(nativeLog), "E:\\", StringComparison.OrdinalIgnoreCase) && !File.Exists(nativeLog))
            {
                Option("log-file", nativeLog);
                Option("msg-level", "all=v,ao/wasapi=trace,vo/gpu-next=v");
            }
            // libmpv's config=no leaves its default platform cache unresolved.
            // Keep native shader files in the application's dedicated cache.
            // They are included in the next managed cache budget pass; mpv
            // also applies its own renderer-specific shader cache policy.
            if (shaderCachePath != null) Option("gpu-shader-cache-dir", shaderCachePath);
            Option("terminal", "no");
            Option("idle", "yes");
            Option("keep-open", "no");
            Option("input-default-bindings", "no");
            Option("input-vo-keyboard", "no");
            Option("osc", "no");
            Option("gapless-audio", "yes");
            Option("prefetch-playlist", "no");
            Option("hwdec", "auto-safe");
            Option("sub-auto", "exact");
            Option("sub-codepage", "gb18030");
            Option("slang", "zh,chi,zho,eng");
            Option("subs-fallback","yes");
            Option("alang", "zh,chi,zho,eng");
            Option("audio-client-name", "HARMOVUE");
            if (window != 0)
                Option("wid", window.ToInt64().ToString());
            else
                Option("vo", "null");
            if (silent)
                Option("ao", "null");
            if (pcm != null)
            {
                Option("ao", "pcm");
                Option("ao-pcm-file", pcm);
                Option("audio-format", "s16");
                Option("audio-samplerate", "44100");
                Option("audio-channels", "stereo");
            }
            if (thumbnail != null)
            {
                Option("vo", "image");
                Option("vo-image-format", "jpg");
                Option("vo-image-outdir", thumbnail);
                Option("audio", "no");
                Option("frames", "1");
                Option("start", "10%");
                // Bound both dimensions: a very tall source must not turn a
                // small thumbnail task into a large temporary cache write.
                Option("vf", "lavfi=[scale=640:640:force_original_aspect_ratio=decrease:reset_sar=1]");
            }
        }
        int result;
        if (tracePreparation) Diagnostics.TracePlayback("native initialize begin");
        using (UiPerformance.Measure("mpv_initialize_call")) result = mpv_initialize(Handle);
        if (tracePreparation) Diagnostics.TracePlayback("native initialize end");
        if (result < 0)
            throw new Exception("播放内核初始化失败：" + result);
    }
    void Option(string n, string v)
    {
        int e = mpv_set_option_string(Handle, n, v);
        if (e < 0)
            throw new Exception($"内核选项 {n}: {e}");
    }
    public string Get(string n) => ReadProperty(Handle, n);
    static string ReadProperty(nint handle, string n)
    {
        var p = mpv_get_property_string(handle, n);
        if (p == 0)
            return "";
        try
        {
            return Marshal.PtrToStringUTF8(p) ?? "";
        }
        finally { mpv_free(p); }
    }
    public double Number(string n) => double.TryParse(Get(n), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
    public void Set(string n, string v)
    {
        int e = mpv_set_property_string(Handle, n, v);
        if (e < 0)
            throw new Exception($"设置 {n} 失败（{e}）");
    }
    public void Set(string n, double v) => Set(n, v.ToString("R", CultureInfo.InvariantCulture));
    public void Command(params string[] args)
    {
        var strings = args.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
        var array = Marshal.AllocHGlobal((strings.Length + 1) * IntPtr.Size);
        try
        {
            for (int i = 0; i < strings.Length; i++)
                Marshal.WriteIntPtr(array, i * IntPtr.Size, strings[i]);
            Marshal.WriteIntPtr(array, strings.Length * IntPtr.Size, 0);
            int e = mpv_command(Handle, array);
            if (e < 0)
                throw new Exception($"播放命令 {args[0]} 失败（{e}）");
        }
        finally { foreach (var p in strings) Marshal.FreeCoTaskMem(p); Marshal.FreeHGlobal(array); }
    }
    public void Poll()
    {
        PollDrained = false;
        for (int i = 0; i < 100; i++)
        {
            var e = Marshal.PtrToStructure<Event>(mpv_wait_event(Handle, 0));
            if (e.Id == 0)
            {
                PollDrained = true;
                break;
            }
            int error = e.Error;
            if (e.Id == 6) StartedEntryId = e.Data == 0 ? null : Marshal.ReadInt64(e.Data);
            if (e.Id == 7)
            {
                EndReason = e.Data == 0 ? -1 : Marshal.ReadInt32(e.Data);
                EndedEntryId = e.Data == 0 ? null : Marshal.ReadInt64(e.Data, 8);
                if (EndReason == 4) error = Marshal.ReadInt32(e.Data, 4);
            }
            EventReceived?.Invoke(e.Id, error);
        }
    }
    public void Dispose()
    {
        if (Handle != 0)
        {
            mpv_terminate_destroy(Handle);
            Handle = 0;
        }
    }
}
public sealed partial class Playback : IDisposable
{
    readonly Store store;
    readonly nint engineWindow;
    readonly bool silentOutput;
    readonly string? pcmOutput;
    Mpv? engine;
    Task<Mpv>? engineLoad;
    Task? enginePreparation;
    bool disposed;
    public Mpv Engine => engine ?? throw new InvalidOperationException("播放内核尚未就绪，请稍候或重试。");
    internal bool EngineReady => engine != null;
    readonly Dictionary<string, string> enginePropertyCache = new(StringComparer.Ordinal);
    bool desiredPaused = true;
    internal string ReadEngineProperty(string name)
    {
        if (engine == null) return "";
        if (loading) return enginePropertyCache.TryGetValue(name, out string? cached) ? cached : "";
        string value = engine.Get(name);
        enginePropertyCache[name] = value;
        return value;
    }
    // Preferences remain in the store and are applied when the engine is attached.
    internal void SetEngineOption(string name, string value)
    {
        if (engine == null) return;
        engine.Set(name, value);
        enginePropertyCache[name] = value;
        if (name == "pause") desiredPaused = value == "yes";
    }
    internal void SetEngineOption(string name, double value) => engine?.Set(name, value);
    public Media? Current { get; private set; }
    public Session Session => store.State.Active == "video" ? store.State.Video : store.State.Music;
    public bool Paused
    {
        get
        {
            if (engine == null || Current == null) return true;
            if (loading) return desiredPaused;
            return desiredPaused = ReadEngineProperty("pause") == "yes" || ReadEngineProperty("idle-active") == "yes";
        }
    }
    public event Action? Changed;
    public event Action<string>? Failed;
    double previous = -1; DateTime lastTick = DateTime.UtcNow; bool loading; bool navigatingBack; int ticks;
    readonly Dictionary<long, int> nativeOccurrences = [];
    Mpv.CueLoadGuard? cueGuard;
    readonly HashSet<Media> checkpointRecords = [];
    readonly List<IDisposable> subtitleLeases = [];
    void ReleaseSubtitleLeases() { foreach (var lease in subtitleLeases) lease.Dispose(); subtitleLeases.Clear(); }
    // A native subtitle/audio-track refresh can enqueue its own demux seek.
    // Keep the latest explicit user seek until that refresh has settled, rather
    // than blocking the UI or allowing the older refresh to overwrite it.
    sealed record SeekIntent(Media Media, long Generation, double Seconds, long RequestedAt);
    SeekIntent? pendingSeek;
    long playbackGeneration, trackQuietUntil;
    const int TrackSettleMilliseconds = 250, MaximumSeekDeferralMilliseconds = 1000;
    static long TimestampAfter(long now, int milliseconds) => now + (long)(Stopwatch.Frequency * milliseconds / 1000d);
    void CancelPendingSeek() { pendingSeek = null; trackQuietUntil = 0; playbackGeneration++; }
    void TrackChanged() => trackQuietUntil = TimestampAfter(Stopwatch.GetTimestamp(), TrackSettleMilliseconds);
    bool stopAtEnd;
    public bool StopAtEnd
    {
        get => stopAtEnd;
        set
        {
            stopAtEnd = value;
            if (value && engine is { } active)
            {
                // A one-shot end timer must be able to reach EOF even when
                // the user previously enabled a file or A/B loop.
                Safe(() => active.Set("ab-loop-a", "no"));
                Safe(() => active.Set("ab-loop-b", "no"));
            }
            ApplyEndPolicy();
        }
    }
    public void ApplyEndPolicy() => ApplyEndPolicy(Current?.Kind);
    void ApplyEndPolicy(string? kind)
    {
        if (engine == null) return;
        engine.Set("keep-open", StopAtEnd || kind == "video" && !store.State.AutoNext ? "always" : "no");
        ApplyRepeatPolicy();
    }
    public void ApplyRepeatPolicy()
    {
        if (engine == null) return;
        engine.Set("loop-file", !StopAtEnd && store.State.Repeat == "one" ? "inf" : "no");
        engine.Set("loop-playlist", !StopAtEnd && store.State.Repeat == "all" && nativeQueue.Count > 0 && nativeOrder.Count == nativeQueue.Count ? "inf" : "no");
    }
    public void CycleAbLoop()
    {
        if (StopAtEnd) return;
        Engine.Command("ab-loop");
    }
    public DateTime? StopAt; public string Error { get; private set; } = "";
    public Playback(Store s, nint window, bool silent = false) : this(s, window, silent, null) { }
    internal Playback(Store s, nint window, bool silent, string? pcm) : this(s, window, silent, pcm, false) { }

    readonly NativeWarmup? nativeWarmup;
    internal Playback(Store s, nint window, bool silent, string? pcm, bool deferEngine, NativeWarmup? nativeWarmup = null)
    {
        using var measure = UiPerformance.Measure("playback_constructor_body");
        store = s;
        engineWindow = window;
        silentOutput = silent;
        pcmOutput = pcm;
        this.nativeWarmup = nativeWarmup;
        if (!deferEngine) AttachEngine(CreateEngine());
    }
    Mpv CreateEngine()
    {
        using var measure = UiPerformance.Measure("playback_engine_construct_call");
        string? shaderCache = null;
        try { shaderCache = MediaCache.NativeShaderDirectory(Path.Combine(store.DirectoryPath, "cache")); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        // Hold the pinned absolute-path mapping through the first native bind.
        // The runtime retains its own reference once the P/Invoke is resolved.
        nativeWarmup?.WaitBeforeNativeLoad();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed), this);
        Diagnostics.TracePlayback("native load begin");
        nint module = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll"));
        Diagnostics.TracePlayback("native load end");
        try
        {
            // Loading an image cannot be safely interrupted. If the window closed
            // during that call, release the image without initializing a stale wid.
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed), this);
            return new Mpv(engineWindow, silentOutput, pcmOutput, null, tracePreparation: true, shaderCachePath: shaderCache);
        }
        finally { NativeLibrary.Free(module); }
    }
    // Preparation and publication are owned by the UI thread. Only native
    // construction is delegated, so no worker uses the library or controls.
    internal Task PrepareEngineAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (EngineReady) return Task.CompletedTask;
        if (enginePreparation is { IsCompleted: false }) return enginePreparation;
        Diagnostics.TracePlayback("engine preparation requested");
        return enginePreparation = PrepareEngineCoreAsync();
    }
    async Task PrepareEngineCoreAsync()
    {
        var load = engineLoad = Task.Run(CreateEngine);
        try
        {
            var created = await load;
            ObjectDisposedException.ThrowIf(disposed, this);
            AttachEngine(created);
        }
        finally { engineLoad = null; }
    }
    void AttachEngine(Mpv created)
    {
        engine = created;
        try
        {
            using (UiPerformance.Measure("playback_sub_auto")) created.Set("sub-auto", "no");
            created.EventReceived += OnEvent;
            using (UiPerformance.Measure("playback_apply_settings")) ApplySettings();
            Diagnostics.TracePlayback("engine ready");
        }
        catch
        {
            engine = null;
            created.Dispose();
            throw;
        }
    }
    public void ApplySettings()
    {
        if (engine == null) return;
        engine.Set("volume", store.State.Volume);
        engine.Set("audio-device", store.State.Device);
        engine.Set("slang", store.State.SubtitleLanguages);
        engine.Set("alang", store.State.AudioLanguages);
        engine.Set("sub-font-size", store.State.SubtitleSize);
        engine.Set("sub-pos", store.State.SubtitlePosition);
        engine.Set("sub-ass-override", store.State.OverrideAss ? "force" : "no");
        ApplyEndPolicy();
    }
    public void Play(IEnumerable<Media> entries, Media selected, bool resume = false)
    {
        var source = entries.Cast<Media?>().ToList();
        int index = source.IndexOf(selected);
        if (index < 0) { source.Insert(0, selected); index = 0; }
        PlayAt(source, index, resume);
    }
    // A playlist row is an occurrence, not just a media ID. Resolve the selected
    // row before filtering missing records, retaining later duplicate rows.
    public void PlayAt(IReadOnlyList<Media?> entries, int selectedIndex, bool resume = false)
        => PlayAtCore(entries, selectedIndex, resume, null);
    internal void PlayAtPrepared(IReadOnlyList<Media?> entries, int selectedIndex, bool resume, string format)
        => PlayAtCore(entries, selectedIndex, resume, format);
    void PlayAtCore(IReadOnlyList<Media?> entries, int selectedIndex, bool resume, string? format)
    {
        if (selectedIndex < 0 || selectedIndex >= entries.Count) throw new ArgumentOutOfRangeException(nameof(selectedIndex));
        var selected = entries[selectedIndex] ?? throw new FileNotFoundException("此项记录已失联，请重新添加对应文件。");
        var queue = new List<Media>();
        int index = -1;
        for (int i = 0; i < entries.Count; i++)
        {
            // Future entries stay in the queue, including files that may
            // reconnect before their turn. Never probe their drives here.
            var media = i == selectedIndex ? selected : entries[i];
            if (media == null || media.Kind != selected.Kind) continue;
            if (i == selectedIndex) index = queue.Count;
            queue.Add(media);
        }
        if (store.State.Shuffle)
        {
            queue.RemoveAt(index);
            var shuffled = queue.ToArray();
            Random.Shared.Shuffle(shuffled);
            queue = [selected, .. shuffled];
            index = 0;
        }
        StartQueue(queue, index, resume, preparedFormat: format);
    }
    void StartQueue(List<Media> queue, int index, bool resume, List<int>? history = null, double? positionOverride = null, bool paused = false, string? preparedFormat = null)
    {
        var selected = queue[index];
        if (selected.CueTrackMissing)
            throw new FileNotFoundException("CUE 文件中已无此曲目，请检查 CUE 并重新扫描。", selected.Cue);
        if (preparedFormat == null && !selected.Online)
            throw new FileNotFoundException("文件暂时离线。请连接媒体盘或重新定位。", selected.Path);
        selected.FormatNote = preparedFormat ?? WavPackInfo.Describe(selected.Path);
        if (cueGuard == null && queue.Any(m => m.Cue.Length != 0))
            cueGuard = new Mpv.CueLoadGuard(Engine, store);
        CancelPendingSeek();
        CancelSubtitles();
        SavePosition();
        endTimerHeld = false;
        var target = selected.Kind == "video" ? store.State.Video : store.State.Music;
        var savedSession = (Active: store.State.Active, Queue: target.Queue, Index: target.Index, History: target.History);
        string savedError = Error;
        long activationGeneration = playbackGeneration;
        bool published = false;
        var ids = queue.Select(m => m.Id).ToList();
        Diagnostics.TracePlayback("first play queue prepare begin", selected.Id, activationGeneration);
        Engine.Set("pause", "yes");
        Engine.Command("stop");
        cueGuard?.ClearPaths();
        ResetNativeQueue(queue);
        try
        {
            ApplyEndPolicy(selected.Kind);
            Engine.Set("vid", selected.Kind == "music" ? "no" : "auto");
            Engine.Set("speed", target.Speed);
            double position = positionOverride ?? (resume ? selected.Position : 0);
            // Invalid or near-EOF resume checkpoints otherwise make the
            // chosen file finish immediately or fail to open.
            bool nearEndResume = resume && !positionOverride.HasValue && selected.Duration > 1 && position >= selected.Duration - .5;
            if (!double.IsFinite(position) || position < 0 || selected.Duration > 0 && position >= selected.Duration || nearEndResume) position = 0;
            // Populate the idle playlist first. Starting the selected file here
            // would make later native queue/property calls wait behind AO/VO init.
            AddNative(index, position, startIfEmpty: false);
            if (queue.Count <= 128)
                for (int i = 0; i < queue.Count; i++) AddNative(i);
            else
                EnsureSuccessors(index);
            ApplyRepeatPolicy();
            Engine.Set("pause", paused ? "yes" : "no");
            int activationIndex = nativeOrder.BinarySearch(index);
            if (activationIndex < 0) throw new InvalidOperationException("所选队列条目未准备完成。");

            // Publish the real requested occurrence before callbacks, without
            // consulting a core that is about to open its output devices.
            published = true;
            store.State.Active = selected.Kind;
            target.Queue = ids;
            target.Index = index;
            target.History = history ?? [];
            navigatingBack = false;
            Current = selected;
            SetCurrentOccurrence(index);
            loading = true;
            desiredPaused = paused;
            enginePropertyCache.Clear();
            Error = "";
            previous = -1;
            lastTick = DateTime.UtcNow;
            Changed?.Invoke();
            // A callback may clear playback or issue a newer play request.
            // Never activate this now-stale native index over that newer intent.
            if (playbackGeneration != activationGeneration || !ReferenceEquals(Current, selected) || !ReferenceEquals(target.Queue, ids)) return;
            Diagnostics.TracePlayback("first play queue prepared", selected.Id, activationGeneration);
            // This is the final native operation. The initial state callbacks
            // and successor/policy setup have already run while the core is idle.
            Engine.Command("playlist-play-index", activationIndex.ToString(CultureInfo.InvariantCulture));
            Diagnostics.TracePlayback("playlist activation submitted", selected.Id, activationGeneration);
        }
        catch
        {
            // A superseding callback owns its own queue and cleanup now.
            if (playbackGeneration != activationGeneration) throw;
            Safe(() => Engine.Command("stop"));
            cueGuard?.ClearPaths();
            nativeOccurrences.Clear();
            ResetNativeQueue([]);
            if (published)
            {
                store.State.Active = savedSession.Active;
                target.Queue = savedSession.Queue;
                target.Index = savedSession.Index;
                target.History = savedSession.History;
            }
            Current = null;
            loading = false;
            desiredPaused = true;
            enginePropertyCache.Clear();
            Error = savedError;
            Changed?.Invoke();
            throw;
        }
    }
    void RememberNativePlaylist()
    {
        nativeOccurrences.Clear();
        using var playlist = JsonDocument.Parse(Engine.Get("playlist"));
        int index = 0;
        foreach (var entry in playlist.RootElement.EnumerateArray())
        {
            if (entry.TryGetProperty("id", out var id) && id.TryGetInt64(out long value))
                nativeOccurrences[value] = nativeOrder.Count > index ? nativeOrder[index] : index;
            index++;
        }
    }
    string LoadNative(QueueSource source, double offset = 0, string action = "append", int index = -1)
    {
        string path = source.Path, cue = source.Cue;
        double start = source.Start;
        double? end = source.End;
        string options = "sub-codepage=" + source.Codepage + ",start=" + (start + offset).ToString("R", CultureInfo.InvariantCulture);
        if (end.HasValue) options += ",end=" + end.Value.ToString("R", CultureInfo.InvariantCulture);
        if (cue.Length != 0)
        {
            cueGuard ??= new Mpv.CueLoadGuard(Engine, store);
            path = cueGuard.Add(source.Media, path, cue, start, end, source.DeclaredPath);
        }
        try
        {
            Engine.Command("loadfile", path, action, index.ToString(CultureInfo.InvariantCulture), options);
            if (action == "replace") Diagnostics.TracePlayback("loadfile submitted", source.Media.Id, playbackGeneration);
        }
        catch { if (cue.Length != 0) cueGuard!.Remove(path); throw; }
        return path;
    }
    void OnEvent(int id, int error)
    {
        long? eventEntry = id == 7 ? Engine.EndedEntryId : id == 8 ? Engine.StartedEntryId : null;
        bool skippedCue = id == 7 && eventEntry.HasValue && cueGuard?.ConsumeSkip(eventEntry.Value) == true;
        if (id is 7 or 8 && Current == null) return;
        int index = -1;
        if (eventEntry.HasValue && !nativeOccurrences.TryGetValue(eventEntry.Value, out index)) return;
        if (id == 8 && index >= 0)
        {
            Diagnostics.TracePlayback("file loaded event received", Current?.Id ?? "", playbackGeneration);
            FileLoaded(index);
            if (currentEntry == eventEntry && Current != null)
                Diagnostics.TracePlayback("file loaded", Current.Id, playbackGeneration);
        }
        if (id == 21) Diagnostics.TracePlayback("playback restart event received", Current?.Id ?? "", playbackGeneration);
        if (id == 21 && Diagnostics.PlaybackTraceEnabled && Current != null && TryNativeEntry(out long restarted) && restarted == currentEntry)
            Diagnostics.TracePlayback("playback restart", Current.Id, playbackGeneration);
        if (id == 7)
        {
            // A failed first open can end without FILE_LOADED. Leave preparing
            // mode so the play button and a later retry reflect the idle core.
            if (eventEntry.HasValue && currentEntry == eventEntry && (error < 0 || skippedCue))
            {
                loading = false;
                desiredPaused = true;
                enginePropertyCache.Clear();
            }
            if (Engine.EndReason is 0 or 4 && index >= 0) stagedEndedIndex = index;
            if (skippedCue && index == Session.Index && currentEntry == eventEntry)
            {
                Error = "CUE 文件已变更或离线，请重新扫描后播放。";
                Failed?.Invoke(Error);
            }
            if (error < 0 && !skippedCue)
            {
                Error = "文件播放失败，请检查格式、文件完整性或输出设备。内核代码 " + error;
                Failed?.Invoke(Error);
            }
            if (Engine.EndReason == 0 && !loading && Engine.Get("eof-reached") == "yes" && (StopAtEnd || (!store.State.AutoNext && store.State.Active == "video")))
            {
                Safe(() => Engine.Set("pause", "yes"));
                // Preserve the held EOF until explicit play/seek. Restore the
                // user's repeat preference when that next intent is issued.
                if (StopAtEnd) { stopAtEnd = false; endTimerHeld = true; }
            }
        }
        if (error < 0 && id != 7)
        {
            Error = "播放内核错误：" + error;
            Failed?.Invoke(Error);
        }
    }
    void FileLoaded(int index)
    {
        if (index >= Session.Queue.Count) return;
        var candidate = store.FindById(Session.Queue[index]);
        if (candidate == null) return;

        // FILE_LOADED can be delivered after the core moves on or reaches
        // idle. Its occurrence determines history, even then. Track commands
        // belong only to the file the core is actually playing.
        bool live = TryNativeEntry(out long liveId) && nativeOccurrences.TryGetValue(liveId, out int liveIndex) && liveIndex == index;
        loading = !live && Engine.Get("idle-active") != "yes";
        CancelPendingSeek();
        if (Session.Index != index && !navigatingBack) Session.History.Add(Session.Index);
        navigatingBack = false;
        if (Current != null) checkpointRecords.Add(Current);
        Session.Index = index;
        Current = candidate;
        SetCurrentOccurrence(index);
        ReleaseSubtitleLeases();
        Error = "";
        previous = -1;
        lastTick = DateTime.UtcNow;
        if (store.State.RecordHistory) candidate.LastPlayed = lastTick;
        if (live)
        {
            Safe(() => Engine.Set("sid", "auto"));
            ScheduleSubtitles();
            Safe(() => Engine.Set("aid", candidate.Audio));
            if (candidate.Subtitle != "auto") Safe(() => Engine.Set("sid", candidate.Subtitle));
            Safe(() => Engine.Set("sub-delay", candidate.SubDelay));
            Safe(() => Engine.Set("audio-delay", candidate.AudioDelay));
            TrackChanged();
        }
        Changed?.Invoke();
        EnsureSuccessors(index);
    }
    static void Safe(Action a)
    {
        try
        {
            a();
        }
        catch { }
    }
    long? tracedOutputEntry;
    bool tracedAudioPts, tracedVideoConfigured;
    void TraceFirstOutput()
    {
        if (!Diagnostics.PlaybackTraceEnabled || Current == null || loading ||
            !TryNativeEntry(out long entry) || entry != currentEntry) return;
        if (tracedOutputEntry != entry)
        {
            tracedOutputEntry = entry;
            tracedAudioPts = tracedVideoConfigured = false;
        }
        if (!tracedAudioPts && double.TryParse(Engine.Get("audio-pts"), NumberStyles.Float, CultureInfo.InvariantCulture, out double audioPts) && double.IsFinite(audioPts))
        {
            tracedAudioPts = true;
            Diagnostics.TracePlayback("first audio pts", Current.Id, playbackGeneration);
            Diagnostics.TracePlayback("audio output details", Current.Id, playbackGeneration,
                Engine.Get("current-ao") + "; " + Engine.Get("audio-out-params"));
        }
        // This is a backend readiness marker, not proof of a visible frame.
        // Video surface capture is checked independently during acceptance.
        if (!tracedVideoConfigured && Current.Kind == "video" && Engine.Get("vo-configured") == "yes"
            && double.TryParse(Engine.Get("video-out-params/w"), NumberStyles.Float, CultureInfo.InvariantCulture, out double width) && width > 0
            && double.TryParse(Engine.Get("time-pos/full"), NumberStyles.Float, CultureInfo.InvariantCulture, out double position) && double.IsFinite(position))
        {
            tracedVideoConfigured = true;
            Diagnostics.TracePlayback("video output configured", Current.Id, playbackGeneration);
            Diagnostics.TracePlayback("video output details", Current.Id, playbackGeneration,
                Engine.Get("current-vo") + "; " + Engine.Get("current-gpu-context") + "; " + Engine.Get("hwdec-current")
                + "; shader-cache=" + Engine.Get("gpu-shader-cache-dir"));
        }
    }

    public void Tick()
    {
        if (engine == null) return;
        engine.Poll();
        TraceFirstOutput();
        ContinueStagedQueue();
        CompleteSubtitles();
        PumpNativeQueue();
        DispatchPendingSeek();
        var now = DateTime.UtcNow;
        double elapsed = (now - lastTick).TotalSeconds;
        lastTick = now;
        if (Current != null && TryCurrentPosition(out double pos))
        {
            currentPosition = pos;
            bool sameSource = MatchesCurrentRecord();
            if (sameSource && !Paused && previous >= 0 && pos > previous && Math.Abs(pos - previous - elapsed * Session.Speed) < .3 && elapsed < 2 && Engine.Get("seeking") != "yes")
            {
                Current.WatchedSeconds += pos - previous;
                if (Current.Kind == "video" && !Current.WatchManual && Current.WatchedSeconds >= CurrentDuration * .9 && CurrentDuration > 0)
                    Current.Watch = "已看";
            }
            if (sameSource && pos > 1 && Current.Watch == "未看" && Current.Kind == "video" && !Current.WatchManual)
                Current.Watch = "未看完";
            if (sameSource) { Current.Position = pos; Session.Position = pos; }
            previous = pos;
            if (sameSource && (!double.IsFinite(Current.Duration) || Current.Duration <= 0)
                && TryNativeEntry(out long durationEntry) && durationEntry == currentEntry)
            {
                double duration = (currentSource?.End ?? Engine.Number("duration")) - (currentSource?.Start ?? 0);
                // A native automatic transition can occur between property reads.
                // Only publish metadata learned from this exact occurrence.
                if (double.IsFinite(duration) && duration > 0 && MatchesCurrentRecord()
                    && TryNativeEntry(out long afterDuration) && afterDuration == durationEntry)
                {
                    Current.Duration = duration;
                    try { store.SaveMetadata(Current); }
                    finally { Changed?.Invoke(); }
                }
            }
        }
        if (StopAt.HasValue && DateTime.Now >= StopAt.Value)
        {
            Engine.Set("pause", "yes");
            StopAt = null;
        }
        // keep-open can hold a decoded final frame before mpv emits END_FILE.
        // Clear the one-shot timer against the actual held occurrence too.
        if (StopAtEnd && !loading && TryNativeEntry(out long endEntry) && endEntry == currentEntry && Engine.Get("eof-reached") == "yes")
        {
            Engine.Set("pause", "yes"); stopAtEnd = false; endTimerHeld = true;
        }
        if (++ticks % 50 == 0)
            SaveCheckpoint();
    }
    public void SavePosition()
    {
        if (Current != null && TryCurrentPosition(out double pos))
        {
            currentPosition = pos;
            if (MatchesCurrentRecord()) { Current.Position = pos; Session.Position = pos; }
        }
        SaveCheckpoint();
    }
    void SaveCheckpoint()
    {
        if (Current != null) checkpointRecords.Add(Current);
        store.SavePlaybackBatch(checkpointRecords);
        // If persistence fails, retain the set so a later checkpoint can retry.
        checkpointRecords.Clear();
    }
    public void Clear()
    {
        CancelPendingSeek();
        engine?.Command("stop");
        Current = null;
        loading = false;
        desiredPaused = true;
        enginePropertyCache.Clear();
        previous = -1;
        Error = ""; StopAt = null; stopAtEnd = false;
        nativeOccurrences.Clear(); cueGuard?.ClearPaths();
        ResetNativeQueue([]); CancelSubtitles(); endTimerHeld = false;
        Changed?.Invoke();
    }
    public void Toggle()
    {
        RestoreAfterEndTimer();
        if (Current == null)
        {
            Resume(store.State.Active);
            return;
        }
        if (!loading && Engine.Get("idle-active") == "yes")
        {
            StartSession(Current.Kind, false);
            return;
        }
        bool nextPaused = loading ? !desiredPaused : Engine.Get("pause") != "yes";
        SetEngineOption("pause", nextPaused ? "yes" : "no");
    }
    public void Resume(string kind) => StartSession(kind, true);
    public void ResumePrepared(string kind, bool resume, string format, double? positionOverride = null, bool paused = false)
        => StartSession(kind, resume, positionOverride, paused, format);
    public double CurrentStart => currentSource?.Start ?? Current?.Start ?? 0;
    public double Position => currentPosition;
    public double CurrentDuration => currentSource is { } source
        ? source.End.HasValue ? Math.Max(0, source.End.Value - source.Start) : source.Duration > 0 ? source.Duration : Current?.Duration ?? 0
        : Current?.Duration ?? 0;
    void StartSession(string kind, bool resume, double? positionOverride = null, bool paused = false, string? preparedFormat = null)
    {
        var session = kind == "video" ? store.State.Video : store.State.Music;
        if (session.Queue.Count == 0)
            return;
        int selectedIndex = Math.Clamp(session.Index, 0, session.Queue.Count - 1);
        var media = store.Media.ToDictionary(m => m.Id);
        var list = new List<Media>();
        var indexMap = new Dictionary<int, int>();
        for (int oldIndex = 0; oldIndex < session.Queue.Count; oldIndex++)
        {
            if (!media.TryGetValue(session.Queue[oldIndex], out var item) || item.Kind != kind) continue;
            indexMap[oldIndex] = list.Count;
            list.Add(item); // Retain duplicate occurrences and offline future entries.
        }
        if (!indexMap.TryGetValue(selectedIndex, out int restoredIndex))
            throw new FileNotFoundException("保存队列中的当前项目已不在媒体库，请从库中重新选择。");
        var history = session.History.Where(indexMap.ContainsKey).Select(i => indexMap[i]).ToList();
        StartQueue(list, restoredIndex, resume, history, positionOverride, paused, preparedFormat);
    }
    public void Next(bool back = false)
    {
        CancelPendingSeek();
        if (back && Session.History.Count > 0)
        {
            int i = Session.History[^1];
            Session.History.RemoveAt(Session.History.Count - 1);
            navigatingBack = true;
            SelectQueueIndex(i);
        }
        else if (store.State.Shuffle && !back && Session.Queue.Count > 1)
        {
            int next;
            do
            {
                next = Random.Shared.Next(Session.Queue.Count);
            } while (next == Session.Index);
            SelectQueueIndex(next);
        }
        else
        {
            int next = Session.Index + (back ? -1 : 1);
            if (next >= 0 && next < nativeQueue.Count) SelectQueueIndex(next);
            else if (store.State.Repeat == "all" && nativeQueue.Count > 0) SelectQueueIndex(back ? nativeQueue.Count - 1 : 0);
            else Engine.Command(back ? "playlist-prev" : "playlist-next", "weak");
        }
    }
    public void AddSubtitle(string path, bool select = true, bool remember = true)
    {
        if (Current == null) return;
        string prepared = Subtitles.Prepare(path, Current.SubCodepage, Path.Combine(store.DirectoryPath, "cache"), out var lease);
        try { Engine.Command("sub-add", prepared, select ? "select" : "auto", Path.GetFileName(path), Subtitles.Language(Current.Path, path)); subtitleLeases.Add(lease); if (select) TrackChanged(); }
        catch { lease.Dispose(); throw; }
        if (select && remember) { if (!Current.ExternalSubtitles.Contains(path, StringComparer.OrdinalIgnoreCase)) Current.ExternalSubtitles.Add(path); Current.Subtitle = Engine.Get("sid"); store.Save(); }
    }
    void LoadSubtitles()
    {
        ScheduleSubtitles();
    }
    void ChoosePreferredSubtitle()
    {
        if (Current?.Kind != "video") return;
        if (Current.Subtitle == "auto")
        {
            var preferred = store.State.SubtitleLanguages.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(Subtitles.NormalizeLanguage).ToArray();
            using var tracks = JsonDocument.Parse(Engine.Get("track-list"));
            var subtitles = tracks.RootElement.EnumerateArray().Where(t => t.GetProperty("type").GetString() == "sub").ToArray();
            int Score(JsonElement track)
            {
                string language = track.TryGetProperty("lang", out var lang) ? Subtitles.NormalizeLanguage(lang.GetString() ?? "") : "";
                int rank = Array.IndexOf(preferred, language);
                return (rank < 0 ? 1000 : rank * 10) + (track.TryGetProperty("default", out var def) && def.GetBoolean() ? 0 : 1);
            }
            if (subtitles.Length > 0) Engine.Set("sid", subtitles.OrderBy(Score).First().GetProperty("id").ToString());
        }
    }
    public void ReloadSubtitles()
    {
        if (Current == null || Engine.Get("idle-active") == "yes") return;
        using var tracks = JsonDocument.Parse(Engine.Get("track-list"));
        foreach (var track in tracks.RootElement.EnumerateArray())
            if (track.GetProperty("type").GetString() == "sub" && track.TryGetProperty("external", out var external) && external.GetBoolean()) Engine.Command("sub-remove", track.GetProperty("id").ToString());
        ReleaseSubtitleLeases();
        Current.Subtitle = "auto"; Engine.Set("sid", "auto"); ScheduleSubtitles(); TrackChanged(); store.SaveMetadata(Current);
    }
    public void SelectTrack(string property, string value)
    {
        if (property is not ("sid" or "aid")) throw new ArgumentException("Only subtitle and audio tracks can be selected here.", nameof(property));
        Engine.Set(property, value);
        if (Current != null && !loading) TrackChanged();
    }
    public void Seek(double seconds)
    {
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (Current == null) return;
        RestoreAfterEndTimer();
        seconds = Math.Max(0, seconds);
        // At natural EOF mpv may already have unloaded the file. Recreate the
        // saved occurrence at the requested position before attempting a seek;
        // retain the established queue, its history and the user's pause flag.
        if (Engine.Get("idle-active") == "yes")
        {
            StartSession(Current.Kind, false, seconds, Engine.Get("pause") == "yes");
            return;
        }
        long now = Stopwatch.GetTimestamp();
        if (pendingSeek != null || now < trackQuietUntil)
        {
            pendingSeek = new SeekIntent(Current, playbackGeneration, seconds, now);
            return;
        }
        SendSeek(Current, seconds);
    }
    void DispatchPendingSeek()
    {
        if (pendingSeek is not { } intent) return;
        if (intent.Generation != playbackGeneration || !ReferenceEquals(Current, intent.Media) || loading || Engine.Get("idle-active") == "yes")
        { pendingSeek = null; return; }
        long now = Stopwatch.GetTimestamp();
        bool expired = now >= TimestampAfter(intent.RequestedAt, MaximumSeekDeferralMilliseconds);
        if (!expired && (now < trackQuietUntil || Engine.Get("seeking") == "yes")) return;
        // Clear before issuing the command: failure is reported once, not retried
        // forever by the timer. A later user seek can always make a fresh request.
        pendingSeek = null;
        trackQuietUntil = 0;
        SendSeek(intent.Media, intent.Seconds);
    }
    void SendSeek(Media media, double seconds)
    {
        double start = ReferenceEquals(Current, media) ? currentSource?.Start ?? media.Start : media.Start;
        Engine.Command("seek", (start + seconds).ToString("R", CultureInfo.InvariantCulture), "absolute+exact");
    }
    public void Append(Media m, bool next)
    {
        var session = m.Kind == "video" ? store.State.Video : store.State.Music;
        int insertion = next ? Math.Clamp(session.Index + 1, 0, session.Queue.Count) : session.Queue.Count;
        if (Current != null && Current.Kind == m.Kind)
        {
            ShiftNativeIndices(insertion, 1);
            nativeQueue.Insert(insertion, QueueSource.From(m));
            try
            {
                if (nativeOrder.Count == nativeQueue.Count - 1 || insertion <= session.Index + 8) AddNative(insertion);
            }
            catch
            {
                nativeQueue.RemoveAt(insertion); ShiftNativeIndices(insertion + 1, -1);
                throw;
            }
            if (fillCursor >= insertion) fillCursor++;
        }
        session.Queue.Insert(insertion, m.Id);
        // History stores occurrences, so inserting before an old occurrence
        // shifts its index even when the inserted media is a duplicate.
        for (int i = 0; i < session.History.Count; i++)
            if (session.History[i] >= insertion) session.History[i]++;
        store.SaveState();
        Changed?.Invoke();
    }
    public void Dispose()
    {
        nativeWarmup?.Dispose();
        if (disposed) return;
        Volatile.Write(ref disposed, true);
        // The UI continuation may never run once the message loop exits.
        // Transfer cleanup of any unpublished handle to the worker scheduler.
        if (engineLoad is { } pending)
            _ = pending.ContinueWith(static load =>
            {
                if (load.IsCompletedSuccessfully) Safe(load.Result.Dispose);
                else _ = load.Exception;
            }, TaskScheduler.Default);
        try
        {
            CancelPendingSeek();
            CancelSubtitles();
            SavePosition();
            cueGuard?.Dispose();
        }
        finally
        {
            engine?.Dispose();
            engine = null;
            ReleaseSubtitleLeases();
        }
    }
}



