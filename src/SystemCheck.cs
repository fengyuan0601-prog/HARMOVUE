// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using Windows.Media.Control;
using System.Text.Json;
namespace LocalMedia;
static class SystemCheck
{
 public static void Run(string data)
 {
  Directory.CreateDirectory(data);var manager=GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().GetAwaiter().GetResult();var session=manager.GetSessions().FirstOrDefault(s=>s.SourceAppUserModelId.Contains("LocalMedia",StringComparison.OrdinalIgnoreCase));if(session==null)throw new Exception("LocalMedia SMTC session not found");var properties=session.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();bool pause=session.TryPauseAsync().AsTask().GetAwaiter().GetResult();Thread.Sleep(800);string paused=session.GetPlaybackInfo().PlaybackStatus.ToString();bool play=session.TryPlayAsync().AsTask().GetAwaiter().GetResult();Thread.Sleep(800);string resumed=session.GetPlaybackInfo().PlaybackStatus.ToString();var result=new{session.SourceAppUserModelId,properties.Title,properties.Artist,PauseRequested=pause,PausedStatus=paused,PlayRequested=play,ResumedStatus=resumed,Note="Windows SMTC metadata/commands; not sound verification"};File.WriteAllText(Path.Combine(data,"smtc-check.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
 }
}
