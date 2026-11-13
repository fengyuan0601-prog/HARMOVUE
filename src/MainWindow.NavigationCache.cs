// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

namespace LocalMedia;

public sealed partial class MainWindow
{
    // Only the two library shells are retained. Media rows, cards and search
    // results are rebuilt from Filter on every render, so metadata/scan changes
    // do not need a second data-cache invalidation system.
    readonly Dictionary<string, LibraryActionsCache> libraryActionsCache = [];
    readonly Dictionary<string, LibraryEmptyCache> libraryEmptyCache = [];
    readonly record struct LibraryChromeEnvironment(bool Light, bool HighContrast, int Dpi, float Scale);
    readonly record struct LibraryActionsState(string Mode, string Group, bool Empty, bool PlayAll, bool Cards, LibraryChromeEnvironment Environment);
    sealed record LibraryActionsCache(FlowLayoutPanel Control, LibraryActionsState State, string[] Filters);
    sealed record LibraryEmptyCache(GlassCanvas Control, LibraryChromeEnvironment Environment, Action Arrange);

    LibraryChromeEnvironment CurrentLibraryChromeEnvironment() => new(UiTheme.IsLight,
        SystemInformation.HighContrast, DeviceDpi, dpiReady ? dpiFactor : DeviceDpi / 96f);

    bool RetainLibraryChrome(Control control)
    {
        if (!libraryActionsCache.Values.Any(entry => ReferenceEquals(entry.Control, control))
            && !libraryEmptyCache.Values.Any(entry => ReferenceEquals(entry.Control, control))) return false;
        return true;
    }
    void HideLibraryChrome()
    {
        foreach (var entry in libraryActionsCache.Values) if (!entry.Control.IsDisposed) entry.Control.Visible = false;
        foreach (var entry in libraryEmptyCache.Values) if (!entry.Control.IsDisposed) entry.Control.Visible = false;
    }

    void DisposeLibraryNavigationCache()
    {
        DisposeLibraryShelves();
        var controls = libraryActionsCache.Values.Select(entry => (Control)entry.Control)
            .Concat(libraryEmptyCache.Values.Select(entry => (Control)entry.Control)).ToArray();
        // Remove ownership before Release so it actually disposes detached HWNDs.
        libraryActionsCache.Clear(); libraryEmptyCache.Clear();
        foreach (var control in controls) if (!control.IsDisposed) Release(control);
    }

    void InvalidateLibraryChromeEnvironment()
    {
        var environment = CurrentLibraryChromeEnvironment();
        // A theme/DPI change also reaches shells currently detached from the
        // form, which the normal active-control palette walk cannot refresh.
        foreach (var pair in libraryActionsCache.ToArray())
            if (pair.Value.State.Environment != environment)
            {
                libraryActionsCache.Remove(pair.Key);
                if (!pair.Value.Control.IsDisposed) Release(pair.Value.Control);
            }
        foreach (var pair in libraryEmptyCache.ToArray())
            if (pair.Value.Environment != environment)
            {
                libraryEmptyCache.Remove(pair.Key);
                if (!pair.Value.Control.IsDisposed) Release(pair.Value.Control);
            }
    }

    FlowLayoutPanel LibraryActions(string mode, bool emptyLibrary)
    {
        bool playAll = visible.Count > 0 && (page == "音乐库" && mode == "歌曲" || group != "全部");
        var state = new LibraryActionsState(mode, group, emptyLibrary, playAll, useCards, CurrentLibraryChromeEnvironment());
        string[] filters = [];
        if (!emptyLibrary && (page == "视频库" || mode == "文件夹"))
        {
            string kind = page == "音乐库" ? "music" : "video";
            filters = LibraryFilterOptions(kind);
        }
        if (libraryActionsCache.TryGetValue(page, out var cached))
        {
            // Include the actual option values: a directory migration or changed
            // category can keep the same media count while changing this menu.
            if (!cached.Control.IsDisposed && cached.State == state && cached.Filters.SequenceEqual(filters))
            {
                if (cached.Control.Parent != content) content.Controls.Add(cached.Control);
                cached.Control.Visible = true; cached.Control.BringToFront();
                return cached.Control;
            }
            libraryActionsCache.Remove(page);
            if (!cached.Control.IsDisposed) Release(cached.Control);
        }
        var actions = Actions();
        // Keep inherited fonts stable while the native controls are detached.
        actions.Font = Font;
        libraryActionsCache.Add(page, new(actions, state, filters));
        foreach (string label in page == "音乐库" ? new[] { "歌曲", "专辑", "歌手", "文件夹" } : new[] { "全部视频", "文件夹" })
        {
            string value = label;
            var tab = (UiButton)Button(label, () => { if (page == "音乐库") libraryMode = value; else videoLibraryMode = value; group = "全部"; selectedList = null; Render(); }, label.Length > 2 ? 98 : 78, 40);
            tab.Font = OwnedFont(10);
            tab.Style = UiButtonStyle.Tab; tab.Selected = mode == label; tab.ForeColor = tab.Selected ? UiTheme.Ink : UiTheme.Muted;
            tab.Margin = new Padding(0, 0, 6, 4); actions.Controls.Add(tab);
        }
        if (!emptyLibrary && group != "全部" && page != "视频库")
            actions.Controls.Add(Button("返回全部", () => { group = "全部"; selectedList = null; Render(); }, 100, 40));
        if (filters.Length > 0)
        {
            var filter = new UiComboBox { Width = 190 };
            filter.Items.AddRange(filters); filter.SelectedItem = group;
            filter.SelectedIndexChanged += (_, _) => { group = filter.Text; Render(); };
            actions.Controls.Add(filter);
        }
        if (playAll) actions.Controls.Add(Button("播放全部", () => { if (visible.Count > 0) Play(visible[0]); }, 110, 40));
        if (!emptyLibrary && page == "视频库")
            actions.Controls.Add(Button(useCards ? "切换列表" : "切换封面", () => { useCards = !useCards; Render(); }, 110, 40));
        return actions;
    }

    void LibraryEmptyState()
    {
        var environment = CurrentLibraryChromeEnvironment();
        if (libraryEmptyCache.TryGetValue(page, out var cached))
        {
            if (!cached.Control.IsDisposed && cached.Environment == environment)
            {
                if (cached.Control.Parent != content) content.Controls.Add(cached.Control);
                cached.Control.Visible = true; cached.Control.BringToFront();
                layoutAlbumShelf = cached.Arrange;
                return;
            }
            libraryEmptyCache.Remove(page);
            if (!cached.Control.IsDisposed) Release(cached.Control);
        }
        var welcome = new GlassCanvas { Dock = DockStyle.Fill, Font = Font };
        content.Controls.Add(welcome); welcome.BringToFront();
        var arrange = AddLibraryEmptyState(welcome, page == "音乐库" ? "把喜欢的音乐，留在这里" : "把喜欢的影像，留在这里");
        libraryEmptyCache.Add(page, new(welcome, environment, arrange));
        layoutAlbumShelf = arrange;
    }
}
