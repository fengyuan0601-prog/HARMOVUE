// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.Diagnostics;
using System.Text.Json;
namespace LocalMedia;

static class EnduranceTest
{
    public static void Run(string data, string source)
    {
        Directory.CreateDirectory(data);
        using var store = new Store(Path.Combine(data, "database"));
        var media = Scanner.Read(source); store.Upsert([media]); media = store.Media[0];
        store.State.Repeat = "one";
        using var player = new Playback(store, 0, true);
        player.Play([media], media);
        var clock = Stopwatch.StartNew(); var samples = new List<object>();
        double next = 0, maxPosition = 0; int resets = 0; double previous = -1;
        while (clock.Elapsed.TotalSeconds < 300)
        {
            player.Tick();
            double position = player.Engine.Number("time-pos");
            maxPosition = Math.Max(maxPosition, position);
            if (previous > position + 1) resets++;
            previous = position;
            if (clock.Elapsed.TotalSeconds >= next)
            {
                using var process = Process.GetCurrentProcess(); process.Refresh();
                samples.Add(new { Seconds = Math.Round(clock.Elapsed.TotalSeconds, 2), PrivateBytes = process.PrivateMemorySize64, WorkingSet = process.WorkingSet64, Handles = process.HandleCount, Threads = process.Threads.Count, Position = position, Error = player.Error });
                File.WriteAllText(Path.Combine(data, "resource-trend.json"), JsonSerializer.Serialize(new { Samples = samples, MaximumPosition = maxPosition, NativeLoopResets = resets, Meaning = "Five-minute production service / actual libmpv decode with null outputs; does not establish UI, physical output or long-term stability" }, new JsonSerializerOptions { WriteIndented = true }));
                next += 30;
            }
            Thread.Sleep(30);
        }
        if (maxPosition < 1 || resets == 0 || player.Error.Length > 0) throw new Exception("持续解码验证未完成或出现内核错误。");
        File.WriteAllText(Path.Combine(data, "result.txt"), "Completed five minutes of actual decode and native repeat using null outputs; resource trend captured. Not physical audio/UI endurance acceptance.");
    }
}
