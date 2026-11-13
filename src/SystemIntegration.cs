// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using Windows.Media;
using Windows.Media.Devices;
using Windows.Devices.Enumeration;
using Microsoft.Win32;
using Windows.Storage;
using Windows.Storage.Streams;
using System.Runtime.InteropServices;
using System.Diagnostics;
namespace LocalMedia;

public sealed class SystemIntegration : IDisposable
{
    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint state);
    readonly SystemMediaTransportControls controls; readonly MainWindow window; readonly Playback player; readonly DeviceWatcher watcher; readonly string data; string current = ""; bool displayRequired;
    MediaPlaybackStatus? lastStatus;
    long lastTimelineAt;
    double lastPosition, lastDuration, lastSpeed = 1;
    bool hasTimeline;
    readonly object coverGate = new();
    CoverRequest? pendingCover;
    bool coverWorkerActive;
    long coverGeneration;
    IDisposable? displayedCoverLease;
    sealed record CoverRequest(string Id, string Path, string Folder, string Kind, long Generation, string? ExplicitPath);
    public string DefaultOutputName { get; private set; } = "系统默认设备";
    volatile string defaultOutputId = "";
    volatile bool disposed;
    public SystemIntegration(MainWindow window, Playback player, string data)
    {
        this.window = window;
        this.player = player;
        this.data = data;
        controls = SystemMediaTransportControlsInterop.GetForWindow(window.Handle);
        controls.IsEnabled = true;
        controls.IsPlayEnabled = true;
        controls.IsPauseEnabled = true;
        controls.IsNextEnabled = true;
        controls.IsPreviousEnabled = true;
        controls.IsStopEnabled = true;
        controls.ButtonPressed += Pressed;
        MediaDevice.DefaultAudioRenderDeviceChanged += DeviceChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        watcher = DeviceInformation.CreateWatcher(MediaDevice.GetAudioRenderSelector());
        watcher.Removed += Removed;
        watcher.Start();
        RefreshDefaultOutput();
    }
    async void RefreshDefaultOutput()
    {
        try
        {
            string id = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
            defaultOutputId = id;
            if (string.IsNullOrEmpty(id)) { DefaultOutputName = "未找到默认输出设备"; return; }
            var device = await DeviceInformation.CreateFromIdAsync(id);
            if (!disposed) DefaultOutputName = string.IsNullOrWhiteSpace(device.Name) ? "系统默认设备" : device.Name;
        }
        catch { if (!disposed) DefaultOutputName = "系统默认设备（名称不可用）"; }
    }
    internal static bool IsRemovedOutput(string selection, string defaultId, string removedId)
    {
        if (string.IsNullOrEmpty(removedId)) return false;
        if (selection is "" or "auto") return !string.IsNullOrEmpty(defaultId) && removedId.Equals(defaultId, StringComparison.OrdinalIgnoreCase);
        // mpv uses wasapi/<endpoint>; WinRT prefixes the same endpoint with
        // a device-interface path. Compare the endpoint itself, not names.
        string endpoint = selection.StartsWith("wasapi/", StringComparison.OrdinalIgnoreCase) ? selection[7..] : selection;
        return endpoint.Length > 0 && (removedId.Equals(endpoint, StringComparison.OrdinalIgnoreCase) || removedId.Contains(endpoint, StringComparison.OrdinalIgnoreCase));
    }
    void Removed(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        string removed = update.Id, previousDefault = defaultOutputId;
        PauseForDevice(() => IsRemovedOutput(player.Engine.Get("audio-device"), previousDefault, removed));
    }
    void DeviceChanged(object sender, DefaultAudioRenderDeviceChangedEventArgs args)
    {
        if (args.Role == AudioDeviceRole.Default)
        {
            RefreshDefaultOutput();
            PauseForDevice(() => player.Engine.Get("audio-device") is "" or "auto");
        }
    }
    void PowerChanged(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode == PowerModes.Suspend || args.Mode == PowerModes.Resume)
            PauseForDevice();
    }
    void PauseForDevice(Func<bool>? condition = null)
    {
        if (!window.IsDisposed)
            try { window.BeginInvoke(() => { try { if (condition != null && !condition()) return; window.RequestMediaCommand("pause"); window.Text = "HARMOVUE · 设备或电源状态改变，已暂停"; } catch { } }); } catch (InvalidOperationException) { }
    }
    void Pressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        if (window.IsDisposed)
            return;
        try { window.BeginInvoke(() => window.RequestMediaCommand(args.Button switch { SystemMediaTransportControlsButton.Play => "play", SystemMediaTransportControlsButton.Pause or SystemMediaTransportControlsButton.Stop => "pause", SystemMediaTransportControlsButton.Next => "next", SystemMediaTransportControlsButton.Previous => "previous", _ => "" })); } catch (InvalidOperationException) { }
    }
    public void Update()
    {
        if (disposed) return;
        using var measurement = UiPerformance.Measure("system_integration_update");
        var m = player.Current;
        bool paused = m == null || player.Paused;
        var status = m == null ? MediaPlaybackStatus.Closed : paused ? MediaPlaybackStatus.Paused : MediaPlaybackStatus.Playing;
        bool statusChanged = lastStatus != status;
        var previousStatus = lastStatus;
        if (statusChanged)
        {
            using var statusMeasurement = UiPerformance.Measure("smtc_status_write");
            controls.PlaybackStatus = status;
            lastStatus = status;
        }
        bool trackChanged = current != (m?.Id ?? "");
        if (trackChanged)
        {
            current = m?.Id ?? "";
            lock (coverGate) { coverGeneration++; pendingCover = null; }
            var d = controls.DisplayUpdater;
            d.ClearAll();
            displayedCoverLease?.Dispose(); displayedCoverLease = null;
            if (m != null)
            {
                d.Type = m.Kind == "music" ? MediaPlaybackType.Music : MediaPlaybackType.Video;
                if (m.Kind == "music")
                {
                    d.MusicProperties.Title = m.Title;
                    d.MusicProperties.Artist = m.Artist;
                    d.MusicProperties.AlbumTitle = m.Album;
                }
                else
                    d.VideoProperties.Title = m.Title;
            }
            d.Update();
            if (m != null) QueueCover(m);
        }
        if (m != null)
        {
            long now = Stopwatch.GetTimestamp();
            double elapsed = hasTimeline ? Stopwatch.GetElapsedTime(lastTimelineAt, now).TotalSeconds : 0;
            double position = double.IsFinite(m.Position) ? Math.Max(0, m.Position) : 0;
            double duration = Math.Max(position, double.IsFinite(m.Duration) ? Math.Max(0, m.Duration) : 0);
            double speed = player.Session.Speed;
            if (!double.IsFinite(speed) || speed <= 0) speed = 1;
            double expected = lastPosition + (previousStatus == MediaPlaybackStatus.Playing ? elapsed * lastSpeed : 0);
            bool seeked = hasTimeline && (paused ? Math.Abs(position - lastPosition) > .01 : Math.Abs(position - expected) > 1.25);
            bool changed = trackChanged || statusChanged || !hasTimeline || Math.Abs(duration - lastDuration) > .1 || speed != lastSpeed || seeked;
            // Stable playback is published at most once a second; a paused
            // timeline stays still. Transitions and significant seeks bypass
            // the interval so the system view reflects the user's action.
            if (changed || !paused && elapsed >= 1)
            {
                using var timelineMeasurement = UiPerformance.Measure("smtc_timeline_write", changed ? "transition" : "interval");
                var end = TimeSpan.FromSeconds(duration);
                controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties { StartTime = TimeSpan.Zero, EndTime = end, MinSeekTime = TimeSpan.Zero, MaxSeekTime = end, Position = TimeSpan.FromSeconds(position) });
                lastTimelineAt = now; lastPosition = position; lastDuration = duration; lastSpeed = speed; hasTimeline = true;
            }
        }
        else hasTimeline = false;
        bool required = m?.Kind == "video" && !paused;
        if (required != displayRequired)
        {
            SetThreadExecutionState(required ? 0x80000003 : 0x80000000);
            displayRequired = required;
        }
    }
    public void Cover(Media m, string path)
    {
        if (!disposed && current == m.Id) QueueCover(m, path);
    }
    void QueueCover(Media media, string? explicitPath = null)
    {
        lock (coverGate)
        {
            if (disposed) return;
            // One active reader and one replaceable latest request: rapid Next
            // commands cannot create an unbounded set of file/TagLib workers.
            pendingCover = new CoverRequest(media.Id, media.Path, media.Folder, media.Kind, ++coverGeneration,
                explicitPath ?? (string.IsNullOrEmpty(media.CoverPath) ? null : media.CoverPath));
            if (coverWorkerActive) return;
            coverWorkerActive = true;
            _ = Task.Run(ReadCoverRequests);
        }
    }
    bool CoverIsCurrent(CoverRequest request)
    {
        lock (coverGate) return !disposed && request.Generation == coverGeneration;
    }
    async Task ReadCoverRequests()
    {
        while (true)
        {
            CoverRequest request;
            lock (coverGate)
            {
                if (disposed || pendingCover == null) { coverWorkerActive = false; return; }
                request = pendingCover; pendingCover = null;
            }
            IDisposable? lease = null;
            try
            {
                using var measurement = UiPerformance.Measure("smtc_cover_background", request.Kind);
                string? cover = FindCover(request);
                if (cover == null || !CoverIsCurrent(request)) continue;
                lease = MediaCache.Acquire(Path.Combine(data, "cache"), cover);
                var file = await StorageFile.GetFileFromPathAsync(cover);
                if (!CoverIsCurrent(request) || window.IsDisposed || !window.IsHandleCreated) continue;
                var thumbnail = RandomAccessStreamReference.CreateFromFile(file);
                var completionLease = lease;
                window.BeginInvoke((Action)(() =>
                {
                    // Recheck on the UI thread; queued completion may outlive
                    // this track, a clear operation, or the integration itself.
                    bool accepted = false;
                    try
                    {
                        if (!CoverIsCurrent(request) || window.IsDisposed || current != request.Id) return;
                        using var publish = UiPerformance.Measure("smtc_cover_publish");
                        controls.DisplayUpdater.Thumbnail = thumbnail;
                        controls.DisplayUpdater.Update();
                        displayedCoverLease?.Dispose();
                        displayedCoverLease = completionLease;
                        accepted = true;
                    }
                    catch { }
                    finally { if (!accepted) completionLease.Dispose(); }
                }));
                lease = null; // The UI completion now owns this cache lease.
            }
            catch { /* Optional cover failure must not interrupt playback. */ }
            finally { lease?.Dispose(); }
        }
    }
    string? FindCover(CoverRequest request)
    {
        if (!CoverIsCurrent(request)) return null;
        if (request.ExplicitPath != null) return File.Exists(request.ExplicitPath) ? request.ExplicitPath : null;
        string cache = Path.Combine(data, "cache", request.Id + ".jpg");
        foreach (string path in new[] { cache, Path.ChangeExtension(request.Path, ".jpg"), Path.Combine(request.Folder, "cover.jpg"), Path.Combine(request.Folder, "folder.jpg") })
        {
            if (!CoverIsCurrent(request)) return null;
            if (File.Exists(path)) return path;
        }
        if (request.Kind != "music" || !CoverIsCurrent(request)) return null;
        using var tags = TagLib.File.Create(request.Path);
        if (tags.Tag.Pictures.Length == 0 || !CoverIsCurrent(request)) return null;
        byte[] bytes = tags.Tag.Pictures[0].Data.Data;
        // A system thumbnail should not write an arbitrarily large tag image.
        if (bytes.Length == 0 || bytes.Length > 16 * 1024 * 1024 || !CoverIsCurrent(request)) return null;
        if (!CoverIsCurrent(request) || !MediaCache.TryWrite(Path.GetDirectoryName(cache)!, cache, bytes)) return null;
        return CoverIsCurrent(request) ? cache : null;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lock (coverGate) { coverGeneration++; pendingCover = null; }
        watcher.Removed -= Removed;
        watcher.Stop();
        MediaDevice.DefaultAudioRenderDeviceChanged -= DeviceChanged;
        SystemEvents.PowerModeChanged -= PowerChanged;
        controls.ButtonPressed -= Pressed;
        controls.IsEnabled = false;
        displayedCoverLease?.Dispose(); displayedCoverLease = null;
        SetThreadExecutionState(0x80000000);
    }
}


