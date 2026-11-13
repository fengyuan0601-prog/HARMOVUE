// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

namespace LocalMedia;

static class PlaybackTransitionTest
{
    public static void Run(string data, string sample)
    {
        string fixture = Path.Combine(data, "fixture"); Directory.CreateDirectory(fixture);
        string a = Path.Combine(fixture, "first.mp4"), b = Path.Combine(fixture, "second.mp4"); File.Copy(sample, a, true); File.Copy(sample, b, true);
        var results = new List<string>();
        using var store = new Store(Path.Combine(data, "database"));
        var first = Scanner.Read(a); first.Duration = 2; first.End = 2;
        var second = Scanner.Read(b); second.Duration = 2; second.End = 2;
        store.Upsert([first, second]);
        using var player = new Playback(store, 0, true);
        void Check(bool ok, string text)
        {
            results.Add((ok ? "PASS " : "FAIL ") + text);
            if (!ok) results.Add($"native path={player.Engine.Get("path")} time={player.Engine.Get("time-pos")} pause={player.Engine.Get("pause")} idle={player.Engine.Get("idle-active")} eof={player.Engine.Get("eof-reached")} playlist={player.Engine.Get("playlist")} error={player.Error}");
            File.WriteAllLines(Path.Combine(data, "playback-transitions.txt"), results);
            if (!ok) throw new Exception(text);
        }
        bool Until(Func<bool> condition, double timeout = 5)
        {
            DateTime end = DateTime.UtcNow.AddSeconds(timeout);
            while (DateTime.UtcNow < end) { player.Tick(); if (condition()) return true; Thread.Sleep(20); }
            return false;
        }
        Check(player.Paused && player.Current == null, "new playback service stays silent with no active item");
        int cleared = 0; player.Changed += () => { if (player.Current == null) cleared++; };
        store.State.Volume = 12; store.State.SubtitleLanguages = "eng"; store.State.AudioLanguages = "eng"; store.State.SubtitleSize = 44; store.State.SubtitlePosition = 85;
        string settingsBackup = Path.Combine(data, "settings-backup.db"); store.Backup(settingsBackup);
        store.State.Volume = 55; player.ApplySettings();
        Check(player.Engine.Number("volume") == 55, "changed settings actually applied to native service");
        player.Clear(); store.Restore(settingsBackup); player.ApplySettings();
        Check(cleared == 1 && player.Current == null && player.StopAt == null && !player.StopAtEnd, "clearing playback notifies UI and clears transient timer state");
        first = store.Media.Single(m => m.Path == a); second = store.Media.Single(m => m.Path == b);
        Check(player.Engine.Number("volume") == 12 && player.Engine.Get("slang") == "eng" && player.Engine.Get("alang") == "eng" && player.Engine.Number("sub-font-size") == 44 && player.Engine.Number("sub-pos") == 85 && player.Paused, "restored volume/languages/subtitle appearance applied to existing service without starting output");
        player.PlayAtPrepared([first, null, second, first], 3, false, "");
        Check(player.Current?.Id == first.Id && player.Session.Index == 2 && player.Session.Queue.SequenceEqual(new[] { first.Id, second.Id, first.Id }), "background-prepared queue preserves order and selected duplicate occurrence");
        second.Position = 12; player.Play([second], second, true);
        Check(Until(() => player.Engine.Get("path") == second.Path && player.Engine.Number("time-pos") > .1) && player.Error == "", "out-of-range saved position restarts instead of opening past EOF");
        second.Position = 0;
        player.Play([first], first); Check(Until(() => player.Engine.Get("path") == first.Path && player.Engine.Number("time-pos") > .1), "different video actually loaded before near-EOF resume");
        second.Position = 1.8; player.Play([second], second, true);
        Check(Until(() => player.Engine.Get("path") == second.Path && player.Engine.Number("time-pos") > .1 && player.Engine.Number("time-pos") < 1.4) && player.Error == "", "near-EOF checkpoint replays from the beginning");
        second.Position = 0;
        player.Play([first, second], first); Check(Until(() => player.Engine.Number("time-pos") > .15), "first video actually decoded");
        store.State.AutoNext = false; player.ApplyEndPolicy();
        Check(Until(() => player.Engine.Get("eof-reached") == "yes" && player.Current?.Id == first.Id && player.Paused), "disabling auto-next during playback holds current video at real EOF");
        player.StopAtEnd = false; player.StopAt = DateTime.Now.AddMinutes(30);
        Check(player.Engine.Get("keep-open") == "always", "minute timer does not override video auto-next-off policy");
        store.State.AutoNext = true; player.StopAt = null; player.ApplyEndPolicy();
        player.Play([first, second], first);
        Check(Until(() => player.Current?.Id == second.Id && player.Engine.Number("time-pos") > .1), "auto-next enabled advances to actual second file");
        player.Play([first, second], first); player.StopAtEnd = true;
        Check(Until(() => player.Engine.Get("eof-reached") == "yes" && player.Current?.Id == first.Id && player.Paused), "enabling end-of-item timer during playback stops before next file");
        player.StopAtEnd = false;
        for (int i = 0; i < 12; i++) player.Play([first, second], i % 2 == 0 ? first : second);
        Check(Until(() => player.Current?.Id == second.Id && player.Engine.Get("path") == second.Path && player.Engine.Number("time-pos") > .1) && player.Error == "", "rapid repeated opens settle on requested final file without stale error");
        string broken = Path.Combine(fixture, "broken.mp4"); File.WriteAllText(broken, "generated invalid media");
        var bad = new Media { Path = broken, Title = "Generated broken sample", Kind = "video" }; store.Upsert([bad]);
        player.Play([bad], bad); Check(Until(() => player.Error.Length > 0), "invalid file produces explicit native playback failure");
        player.Play([first], first);
        Check(Until(() => player.Current?.Id == first.Id && player.Engine.Number("time-pos") > .1) && player.Error == "", "valid playback recovers after decoder failure");
        player.Seek(.5); Check(Until(() => player.Engine.Number("time-pos") >= .5), "exact seek still works after rapid opens and decoder recovery");
        int lateFailures = 0; player.Failed += _ => lateFailures++;
        player.Play([bad], bad); Thread.Sleep(100); // Let native failure queue without delivering events yet.
        player.Play([first], first);
        File.WriteAllText(Path.Combine(data, "native-playlist.json"), player.Engine.Get("playlist"));
        Check(Until(() => player.Current?.Id == first.Id && player.Engine.Number("time-pos") > .1) && player.Error == "" && lateFailures == 0, "queued error from superseded bad file cannot overwrite newer valid playback or notify failure");
        int queueFailures = 0; player.Failed += _ => queueFailures++;
        player.Play([bad, first], bad);
        Check(Until(() => player.Current?.Id == first.Id && player.Engine.Number("time-pos") > .1) && player.Error == "" && queueFailures == 1, "actual bad item in current queue reports once, then next valid item recovers without stale error state");
        player.Clear();
        var missingCue = new Media { Path = first.Path, Cue = Path.Combine(fixture, "missing.cue"), Track = 2, CueTrackMissing = true, Kind = "video", Title = "Invalid virtual track", Start = 0, End = 2, Duration = 2 };
        store.Upsert([missingCue]);
        store.State.Active = "video"; store.State.AutoNext = true; store.State.Video.Queue = [first.Id, missingCue.Id, second.Id]; store.State.Video.Index = 0;
        bool playedMissingCue = false; int staleCueFailures = queueFailures;
        player.Resume("video");
        Check(Until(() => { if (player.Current?.Id == missingCue.Id) playedMissingCue = true; return player.Current?.Id == second.Id && player.Engine.Number("time-pos") > .1; })
            && !playedMissingCue && queueFailures == staleCueFailures && store.State.Video.Queue.SequenceEqual(new[] { first.Id, missingCue.Id, second.Id }),
            "restored queue silently skips a removed CUE occurrence while retaining its saved position");

        // These generated files are in the isolated fixture directory. The
        // audio path still exists in both cases, reproducing the old misplay.
        string sidecar = Path.Combine(fixture, "vanishing.cue");
        File.WriteAllText(sidecar, "FILE \"first.mp4\" BINARY\n TRACK 01 AUDIO\n INDEX 01 00:00:00\n TRACK 02 AUDIO\n INDEX 01 00:02:00\n");
        var unmarked = new Media { Path = first.Path, Cue = sidecar, Track = 1, Kind = "video", Title = "Disappearing CUE", Start = 0, End = 2, Duration = 2 };
        store.Upsert([unmarked]);
        int nativeLoads = 0;
        player.Engine.EventReceived += (id, _) => { if (id == 8) nativeLoads++; };
        player.PlayAt([first, unmarked, second], 0);
        Check(Until(() => player.Current?.Id == first.Id && player.Engine.Number("time-pos") > .1), "local file starts while its later CUE is still present");
        File.Move(sidecar, sidecar + ".moved");
        Check(Until(() => player.Current?.Id == second.Id && player.Engine.Number("time-pos") > .1)
            && nativeLoads == 2 && !unmarked.CueTrackMissing && queueFailures == staleCueFailures && player.Session.Index == 2,
            "CUE disappearing after queueing never loads its old audio and auto-next keeps order");
        player.Clear();
        store.State.Video.Index = 0;
        first.Position = 0;
        nativeLoads = 0;
        player.Resume("video");
        Check(Until(() => player.Current?.Id == second.Id && player.Engine.Number("time-pos") > .1)
            && nativeLoads == 2 && player.Session.Queue.SequenceEqual(new[] { first.Id, unmarked.Id, second.Id }),
            "saved queue skips a physically missing CUE with no database flag");
        File.Move(sidecar + ".moved", sidecar);
        player.Next(true);
        Check(Until(() => player.Current?.Id == first.Id && player.Engine.Number("time-pos") > .1), "previous still returns to the actual last played occurrence");
        player.Next();
        Check(Until(() => player.Current?.Id == unmarked.Id && player.Engine.Number("time-pos") > .1)
            && player.Engine.Get("stream-open-filename") == unmarked.Path && player.Session.Index == 1,
            "restored CUE is available in the same native playlist without changing queue indices");

        player.PlayAt([first, second], 0);
        Check(SpinWait.SpinUntil(() => player.Engine.Get("path") == first.Path && player.Engine.Number("time-pos") > .1, 5000),
            "first file is playing before delaying delivery of events");
        Check(SpinWait.SpinUntil(() => player.Engine.Get("idle-active") == "yes", 6000),
            "native playback finishes both files before delivering their events");
        player.Tick();
        Check(player.Current?.Id == second.Id && player.Session.Index == 1 && player.Session.History.SequenceEqual(new[] { 0 }),
            "delayed FILE_LOADED events retain the last actual occurrence and history at idle");
        results.Add("Actual production service/libmpv file decode and EOF events with null output. Not GUI, physical sound, subtitle sync or device recovery proof.");
        File.WriteAllLines(Path.Combine(data, "playback-transitions.txt"), results);
    }
}
