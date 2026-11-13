// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.Runtime.InteropServices;

namespace LocalMedia;

static class UiAccessibility
{
    // Only set during the final, non-cancelled process shutdown. Never change
    // the accessible tree while browsing, minimizing, or closing to the tray.
    public static bool Exiting { get; private set; }
    [DllImport("UIAutomationCore.dll")]
    static extern int UiaDisconnectAllProviders();
    public static async Task PrepareExitAsync()
    {
        if (Exiting) return;
        Exiting = true;
        // A slow external accessibility client must not strand the UI in an
        // outbound COM call during exit. Keep pumping the STA while the
        // process-scoped disconnect runs on MTA; process exit owns a timed-out
        // client's remaining resources. Never unload UIAutomationCore here.
        var disconnect = Task.Run(() =>
        {
            using var measurement = UiPerformance.Measure("accessibility_disconnect_all");
            try { UiPerformance.Record("accessibility_disconnect_hresult", 0, UiaDisconnectAllProviders().ToString("X8")); }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or ExternalException)
            { UiPerformance.Record("accessibility_disconnect_unavailable", 0, e.GetType().Name); }
        });
        if (await Task.WhenAny(disconnect, Task.Delay(500)) != disconnect)
            UiPerformance.Record("accessibility_disconnect_pending_at_exit", 500);
        else await disconnect;
    }
}
