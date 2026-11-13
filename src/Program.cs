// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.Text;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
namespace LocalMedia;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        string data = Environment.GetEnvironmentVariable("LOCALMEDIA_DATA") ?? Path.Combine(AppContext.BaseDirectory, "Data");
        Diagnostics.BeginStartupTrace();
        Diagnostics.TraceUi(data, "managed entry");
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Diagnostics.TraceUi(data, "encodings ready");
            ApplicationConfiguration.Initialize();
            Diagnostics.TraceUi(data, "WinForms ready");
            if (args.Contains("--test-search")) { SearchTest.Run(data); return; }
            if (args.Contains("--test-library-data")) { LibraryDataTest.Run(data); return; }
            if (args.Contains("--test-playback-transitions")) { PlaybackTransitionTest.Run(data, args.Last()); return; }
            if (args.Contains("--render-pcm")) { MediaVerification.RenderPcm(data, args.Last()); return; }
            if (args.Contains("--test-subtitle-service")) { SubtitleServiceTest.Run(data, args.Last()); return; }
            if (args.Contains("--endurance-test")) { EnduranceTest.Run(data, args.Last()); return; }
            if (args.Contains("--self-test"))
            {
                SelfTest.Run(data);
                return;
            }
            if(args.Contains("--check-smtc")){SystemCheck.Run(data);return;}
            if (args.Contains("--verify-media"))
            {
                MediaVerification.Run(data, args.Last());
                return;
            }
            if (args.Contains("--service-test"))
            {
                ServiceTest.Run(data, args.Last());
                return;
            }
            if (args.Contains("--check-library"))
            {
                using (var db = new Store(data))
                {
                    var record = new
                    {
                        Count = db.Media.Count,
                        SchemaVersion=db.SchemaVersion,
                        SQLiteVersion=db.SQLiteVersion,
                        Favorites = db.Media.Count(m => m.Favorite),
                        Lists = db.State.Lists.Count,
                        Ids = db.Media.Select(m => m.Id).Order().ToArray(),
                        MusicQueue = db.State.Music.Queue.ToArray(),
                        VideoQueue = db.State.Video.Queue.ToArray()
                    };
                    File.WriteAllText(Path.Combine(data, "library-check.json"), JsonSerializer.Serialize(record));
                }
                return;
            }
            string name = "LocalMedia-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(data).ToUpperInvariant())))[..16];
            using var mutex = new Mutex(true, name, out bool first);
            if (!first)
            {
                using var client = new NamedPipeClientStream(".", name, PipeDirection.Out);
                client.Connect(5000);
                using var writer = new StreamWriter(client);
                writer.WriteLine(JsonSerializer.Serialize(args));
                return;
            }
            using var inbox = new SingleInstanceInbox(name, args);
            Diagnostics.TraceUi(data, "instance inbox ready");
            StartupWindow.Run(data, args, inbox);
            inbox.Dispose();
            mutex.ReleaseMutex();
        }
        catch (Exception e) { Diagnostics.Log(data, e); Environment.ExitCode = 1; if (!args.Any(a => a.StartsWith("--"))) MessageBox.Show("HARMOVUE 无法继续：" + e.Message, "HARMOVUE"); }
        finally { Diagnostics.FlushStartupTrace(data); Diagnostics.FlushPlaybackTrace(data); }
    }
}


