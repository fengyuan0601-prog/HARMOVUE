// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

namespace LocalMedia;

public sealed partial class MainWindow
{
    // Every transport intent supersedes file preparation, including media keys,
    // tray, queue and mini-player. There is only one path that can start output.
    async void Transport(Action action, bool requiresEngine = true)
    {
        if (shuttingDown || restoringLibrary) return;
        CancelPendingPlayback();
        int request = playbackRequest;
        try
        {
            if (requiresEngine) await player.PrepareEngineAsync();
            if (request == playbackRequest && !shuttingDown && !restoringLibrary && !IsDisposed)
                Try(action);
        }
        catch (Exception error)
        {
            if (request == playbackRequest && !shuttingDown && !IsDisposed) status.Text = error.Message;
        }
    }
    void TogglePlayback()
    {
        if (shuttingDown || restoringLibrary) return;
        if (player.Current == null || player.Engine.Get("idle-active") == "yes")
            _ = ResumeWithFeedback(player.Current?.Kind ?? store.State.Active, player.Current == null);
        else Transport(player.Toggle);
    }
    void SelectQueue(int index) => Transport(() => player.SelectQueueIndex(index));
    void SeekPlayback(double seconds)
    {
        if (shuttingDown || restoringLibrary || player.Current == null) return;
        if (player.Engine.Get("idle-active") == "yes")
            _ = ResumeWithFeedback(player.Current.Kind, false, seconds, player.Engine.Get("pause") == "yes");
        else Transport(() => player.Seek(seconds));
    }
    async Task<bool> ResumeWithFeedback(string kind, bool resume = true, double? position = null, bool paused = false, bool showPage = false)
    {
        if (shuttingDown || restoringLibrary) return false;
        try
        {
            var session = kind == "video" ? store.State.Video : store.State.Music;
            var entries = session.Queue.Select(store.FindById).ToArray();
            if (entries.Length == 0) { CancelPendingPlayback(); return false; }
            bool started = await PrepareAndPlayCore(entries, Math.Clamp(session.Index, 0, entries.Length - 1), resume, null,
                format => player.ResumePrepared(kind, resume, format, position, paused));
            if (started && showPage && !shuttingDown) { page = "正在播放"; Render(); SyncVideoPresentation(); }
            return started;
        }
        catch (Exception e) { if (!shuttingDown && !IsDisposed) { status.Text = e.Message; messageUntil = DateTime.Now.AddSeconds(8); } return false; }
    }
    internal void RequestMediaCommand(string command)
    {
        switch (command)
        {
            case "toggle": TogglePlayback(); break;
            case "play": if (player.Current == null || player.Engine.Get("idle-active") == "yes") _ = ResumeWithFeedback(player.Current?.Kind ?? store.State.Active); else if (player.Paused) TogglePlayback(); else CancelPendingPlayback(); break;
            case "pause": Transport(() => player.SetEngineOption("pause", "yes"), requiresEngine: false); break;
            case "next": Transport(() => player.Next()); break;
            case "previous": Transport(() => player.Next(true)); break;
        }
    }
}
