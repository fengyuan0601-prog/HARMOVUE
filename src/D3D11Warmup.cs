// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.Runtime.InteropServices;

namespace LocalMedia;

// Warm only the process-local hardware driver path when video is requested.
// No playback commands, HWND, swapchain, output, retained device or UI wait.
internal sealed class D3D11Warmup : IDisposable
{
    static readonly uint[] Levels = [0xc100, 0xc000, 0xb100, 0xb000, 0xa100, 0xa000, 0x9300, 0x9200, 0x9100];
    static readonly HashSet<string> VideoExtensions = new(".mp4 .mkv .mov .avi .webm .flv .wmv .asf .mpg .mpeg .ts .m2ts .rm .rmvb .m4v".Split(' '), StringComparer.OrdinalIgnoreCase);
    int stopped, requested;

    // Preserve the original first explicit file's precedence over a saved page.
    // Unknown/folder arguments conservatively suppress speculative page warming;
    // actual classified video requests can still start it later. No filesystem I/O.
    internal static bool? FirstExplicitVideoIntent(IEnumerable<string> args)
    {
        foreach (string path in args)
        {
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith("--", StringComparison.Ordinal)) continue;
            return VideoExtensions.Contains(Path.GetExtension(path));
        }
        return null;
    }

    internal void Request()
    {
        if (Volatile.Read(ref stopped) != 0 || Interlocked.CompareExchange(ref requested, 1, 0) != 0) return;
        if (Environment.GetEnvironmentVariable("LOCALMEDIA_DISABLE_GPU_WARMUP") == "1")
        { Diagnostics.TracePlayback("gpu warmup disabled"); return; }
        Diagnostics.TracePlayback("gpu warmup requested");
        var work = Task.Run(Run); // Never pass a scheduling cancellation token.
        _ = work.ContinueWith(failed =>
        {
            Diagnostics.TracePlayback("gpu warmup unavailable: worker", detail: failed.Exception!.GetBaseException().HResult.ToString("X8"));
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    void Run()
    {
        try
        {
            if (Volatile.Read(ref stopped) != 0) { Diagnostics.TracePlayback("gpu warmup cancelled before start"); return; }
            Diagnostics.TracePlayback("gpu warmup begin");
            foreach (int first in new[] { 0, 2, 3 })
            {
                if (Volatile.Read(ref stopped) != 0) { Diagnostics.TracePlayback("gpu warmup cancelled before attempt"); return; }
                uint[] levels = first == 0 ? Levels : Levels[first..];
                nint device = 0;
                int result;
                bool created;
                try
                {
                    Diagnostics.TracePlayback("gpu warmup attempt begin", detail: "hardware;flags=0;max=" + levels[0].ToString("X4"));
                    // Same normal presenting call as pinned mpv: NULL adapter,
                    // HARDWARE, flags=0, full descending levels, device output only.
                    // Deliberately no WARP or debug-layer warmup.
                    result = D3D11CreateDevice(0, 1, 0, 0, levels, (uint)levels.Length, 7, out device, 0, 0);
                    created = result >= 0 && device != 0;
                    Diagnostics.TracePlayback("gpu warmup attempt end", detail: "hresult=0x" + ((uint)result).ToString("X8") + ";device=" + (device != 0));
                }
                finally
                {
                    // The driver call cannot be cancelled. Even a failed call or
                    // cancellation while inside it releases every returned ref here.
                    if (device != 0)
                    {
                        Marshal.Release(device);
                        Diagnostics.TracePlayback("gpu warmup device released");
                    }
                }
                if (created) { Diagnostics.TracePlayback("gpu warmup end"); return; }
                // Retry only unsupported feature-list cases; device removal or
                // unrelated failure should fall through to normal mpv untouched.
                if (result != unchecked((int)0x80070057) && result != unchecked((int)0x887A0004))
                { Diagnostics.TracePlayback("gpu warmup unavailable"); return; }
            }
            Diagnostics.TracePlayback("gpu warmup unavailable: feature levels");
        }
        catch (DllNotFoundException error) { Unavailable(error); }
        catch (EntryPointNotFoundException error) { Unavailable(error); }
        catch (BadImageFormatException error) { Unavailable(error); }
        finally { Diagnostics.TracePlayback("gpu warmup completion"); }
    }

    static void Unavailable(Exception error) => Diagnostics.TracePlayback("gpu warmup unavailable: runtime", detail: error.HResult.ToString("X8"));
    public void Dispose() => Interlocked.Exchange(ref stopped, 1);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("d3d11.dll", ExactSpelling = true)]
    static extern int D3D11CreateDevice(nint adapter, uint driverType, nint software, uint flags,
        [In] uint[] featureLevels, uint featureLevelCount, uint sdkVersion, out nint device,
        nint selectedFeatureLevel, nint immediateContext);
}
