// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

namespace LocalMedia;

internal static class BackgroundPaths
{
    // Native filesystem opens are not always interruptible. Limit outstanding
    // work even after the caller times out, so repeated clicks cannot spawn it.
    static readonly SemaphoreSlim slots = new(2, 2);
    public static async Task<bool> DirectoryExistsAsync(string path, CancellationToken cancel)
    {
        var work = Task.Run(async () =>
        {
            await slots.WaitAsync(cancel).ConfigureAwait(false);
            try { cancel.ThrowIfCancellationRequested(); return Directory.Exists(path); }
            finally { slots.Release(); }
        }, cancel);
        return await work.WaitAsync(cancel).ConfigureAwait(false);
    }
}
