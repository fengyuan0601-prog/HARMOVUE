// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

namespace LocalMedia;

public sealed partial class MainWindow
{
    static string ProductVersionLabel => typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "未知版本";
    readonly List<(string Value, UiButton Button)> settingsThemeChoices = [];

    void BuildSettingsPage()
    {
        var panel = new GlassCanvas { Dock = DockStyle.Fill, AutoScroll = true };
        var blocks = new List<Control>();
        void Section(string title)
        {
            var heading = Caption(title, 14); heading.AutoEllipsis = false;
            blocks.Add(heading); panel.Controls.Add(heading);
        }
        Label Row(string title, string description, Control? action = null, int width = 120, bool showDetail = false)
        {
            var heading = Caption(title, 10.5f);
            var detail = Caption(showDetail ? description : "", 9, UiTheme.Muted);
            var row = new SettingsRow(heading, detail, action, width);
            void Explain(Control control)
            {
                control.AccessibleDescription = description;
                tips.SetToolTip(control, description);
                foreach (Control child in control.Controls) Explain(child);
            }
            if (description.Length > 0 && !showDetail) Explain(row);
            blocks.Add(row); panel.Controls.Add(row);
            return detail;
        }
        UiButton Action(string title, Action action, int width = 120) => (UiButton)Button(title, action, width, 36);
        void Toggle(string title, string description, bool value, Action<bool> set)
        {
            var toggle = new SettingsToggle { Checked = value, AccessibleName = title };
            toggle.CheckedChanged += (_, _) => Try(() => { set(toggle.Checked); store.SaveState(); UpdatePlayerChrome(); });
            Row(title, description, toggle, 52);
        }
        SettingsActionGroup ActionsFor(params (string Title, Action Action, int Width)[] entries)
        {
            var group = new SettingsActionGroup();
            foreach (var entry in entries) group.Add(Action(entry.Title, entry.Action, entry.Width), entry.Width);
            return group;
        }

        Section("外观");
        settingsThemeChoices.Clear();
        var themeActions = new SettingsActionGroup();
        foreach (var (value, label) in new[] { ("dark", "深色"), ("light", "浅色") })
        {
            var theme = Action(label, () => SetAppearance(value), 76);
            theme.Style = UiButtonStyle.Tab; theme.Selected = (store.State.Theme == "light" ? "light" : "dark") == value;
            theme.AccessibleName = label + "主题";
            settingsThemeChoices.Add((value, theme));
            themeActions.Add(theme, 76);
        }
        Row("主题", "", themeActions, 160);

        Section("媒体库");
        Row("媒体目录", "只读取影音文件；暂停或移除目录不会删除原文件。", Action("添加目录", AddRoot));
        foreach (var root in store.State.Roots.ToArray())
        {
            var actions = ActionsFor((root.Paused ? "启用" : "暂停", () => { root.Paused = !root.Paused; store.SaveState(); UpdateWatchers(); Render(); }, 64),
                ("重新关联", () => { using var folder = new FolderBrowserDialog { Description = "选择这个媒体目录的新位置" }; if (folder.ShowDialog(this) == DialogResult.OK) { store.Relink(root.Path, folder.SelectedPath); UpdateWatchers(); Render(); } }, 92),
                ("移除", () => { store.State.Roots.Remove(root); store.SaveState(); UpdateWatchers(); Render(); }, 64));
            var rootStatus = Row(root.Path, root.Paused ? "已暂停" : "", actions, 236, showDetail: true);
            if (!root.Paused) RefreshRootAvailability(root.Path, rootStatus);
        }
        Row("重新扫描", "检查已添加目录中的新文件与变更。", ActionsFor(("开始扫描", () => { _ = ScanAll(); }, 108), ("取消扫描", CancelScans, 108)), 224);
        Toggle("自动检查目录", "每 10 分钟增量检查一次。", store.State.PeriodicScan, value => store.State.PeriodicScan = value);
        Toggle("实时监听变化", "文件变化时更新播放器记录。", store.State.WatchFolders, value => { store.State.WatchFolders = value; UpdateWatchers(); });

        Section("播放");
        Toggle("视频连续播放", "当前视频结束后播放队列中的下一项。", store.State.AutoNext, value => { store.State.AutoNext = value; player.ApplyEndPolicy(); });
        Toggle("随机播放", "上一首仍返回实际听过的歌曲。", store.State.Shuffle, value => store.State.Shuffle = value);
        string RepeatName() => store.State.Repeat switch { "all" => "列表循环", "one" => "单曲循环", _ => "关闭" };
        UiButton? repeat = null;
        repeat = Action(RepeatName(), () =>
        {
            string? value = Choose("循环播放", [new Choice("off", "关闭循环"), new Choice("all", "列表循环"), new Choice("one", "单曲循环")], store.State.Repeat);
            if (value == null) return;
            store.State.Repeat = value; player.ApplyRepeatPolicy(); store.SaveState();
            repeat!.Text = RepeatName(); UpdatePlayerChrome();
        });
        Row("循环模式", "应用于当前播放队列。", repeat);
        Row("音频输出", "切换输出设备后暂停，点击播放继续。", Action("选择设备", ChooseDevice));
        Row("字幕与音轨语言", "按填写顺序优先匹配；留空时由播放内核自动选择。", Action("编辑语言", EditLanguagePreferences));

        Section("操作");
        Toggle("关闭后继续播放", "关闭主窗口后留在托盘；默认关闭窗口即退出。", store.State.CloseToTray, value => store.State.CloseToTray = value);
        Row("键盘快捷键", "仅在播放器窗口内生效。", Action("编辑快捷键", EditShortcuts));

        Section("数据");
        Toggle("记录播放历史", "关闭后停止记录，已有历史会保留。", store.State.RecordHistory, value => store.State.RecordHistory = value);
        Row("备份与恢复", "保存媒体库、歌单、收藏、进度和设置；不包含媒体原文件。", ActionsFor(("导出备份", ExportSettingsBackup, 108), ("恢复备份", RestoreSettingsBackup, 108)), 224);
        Row("播放记录", "仅清除播放器记录，不改变文件、歌单或收藏。", ActionsFor(("清除历史", () => { foreach (var media in store.Media) media.LastPlayed = null; store.Save(); status.Text = "播放历史已清除。"; }, 108), ("清除进度", () => { foreach (var media in store.Media) media.Position = 0; store.State.Music.Position = store.State.Video.Position = 0; store.Save(); status.Text = "续播进度已清除。"; }, 108)), 224);
        string cache = Path.Combine(store.DirectoryPath, "cache");
        Label? cacheDetail = null;
        async void RefreshCacheSize()
        {
            try { long size = await MediaCache.GetSizeAsync(cache); if (!panel.IsDisposed && cacheDetail is { IsDisposed: false }) cacheDetail.Text = $"{size / 1048576d:0.0} / {MediaCache.MaximumBytes / 1048576} MiB"; }
            catch (Exception error) { Diagnostics.Log(store.DirectoryPath, error); if (cacheDetail is { IsDisposed: false }) cacheDetail.Text = "暂时无法读取"; }
        }
        async void ClearCache()
        {
            try { await MediaCache.ClearAsync(cache); if (!panel.IsDisposed) { RefreshCacheSize(); status.Text = "缓存已清理；正在使用的字幕会保留。"; } }
            catch (Exception error) { Diagnostics.Log(store.DirectoryPath, error); if (!IsDisposed) status.Text = "缓存暂时无法清理。"; }
        }
        cacheDetail = Row("缓存", "正在计算…", Action("清理缓存", ClearCache), showDetail: true);
        RefreshCacheSize();
        Row("数据位置", store.DirectoryPath, showDetail: true);

        Section("关于");
        Row("HARMOVUE", $"版本 {ProductVersionLabel}", Action("关于", AboutBrand), showDetail: true);

        bool arranging = false;
        void Arrange()
        {
            if (arranging || panel.IsDisposed || dpiReady && !dpiScaled.Contains(panel)) return;
            arranging = true; Point scroll = panel.AutoScrollPosition; panel.SuspendLayout();
            try
            {
                panel.AutoScroll = false; panel.AutoScrollMinSize = Size.Empty;
                float scale = dpiReady ? dpiFactor : 1; int P(float value) => (int)(value * scale);
                int width = Math.Max(P(220), Math.Min(P(960), panel.ClientSize.Width - P(24)));
                int y = 0;
                foreach (var block in blocks)
                {
                    int height = block is SettingsRow row ? row.Arrange(width, scale) : P(56);
                    block.SetBounds(0, y, width, height);
                    if (block is Label label) label.Padding = new Padding(0, P(22), 0, 0);
                    y += height;
                }
                panel.AutoScrollMinSize = new Size(0, y + P(24));
            }
            finally { panel.ResumeLayout(); panel.AutoScroll = true; panel.AutoScrollPosition = new Point(-scroll.X, -scroll.Y); arranging = false; }
        }
        panel.Resize += (_, _) => Arrange(); content.Controls.Add(panel); panel.BringToFront();
        if (IsHandleCreated) BeginInvoke((Action)Arrange);
    }

    async void RefreshRootAvailability(string path, Label target)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(coverLifetime.Token);
        cancel.CancelAfter(TimeSpan.FromSeconds(6));
        try { bool online = await BackgroundPaths.DirectoryExistsAsync(path, cancel.Token); if (!target.IsDisposed && !cancel.IsCancellationRequested) target.Text = online ? "" : "离线"; }
        catch (OperationCanceledException) { if (!target.IsDisposed) target.Text = "暂时无法检查"; }
        catch (Exception error) { Diagnostics.Log(store.DirectoryPath, error); if (!target.IsDisposed) target.Text = "暂时无法检查"; }
    }
    void ExportSettingsBackup()
    {
        using var dialog = new SaveFileDialog { Filter = "HARMOVUE 数据库备份|*.db", InitialDirectory = store.DirectoryPath, FileName = "HARMOVUE-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".db" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        store.Backup(dialog.FileName); status.Text = "备份完成；不包含媒体原文件或缓存。";
    }
    async void RestoreSettingsBackup()
    {
        if (restoringLibrary || shuttingDown) return;
        using var dialog = new OpenFileDialog { Filter = "数据库备份|*.db", InitialDirectory = store.DirectoryPath };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        restoringLibrary = true;
        try
        {
            CancelPendingPlayback(); openingFiles?.Cancel(); CancelScans();
            if (scanCompletion != null) await scanCompletion;
            if (shuttingDown) return;
            await Task.Run(() => store.ValidateBackup(dialog.FileName));
            if (shuttingDown) return;
            timer.Stop(); main.Enabled = nav.Enabled = nowRail.Enabled = playerBar.Enabled = false;
            player.Clear();
            restoreWork = Task.Run(() => store.Restore(dialog.FileName));
            await restoreWork;
            if (shuttingDown) return;
            player.ApplySettings(); volume.Value = Math.Clamp(store.State.Volume, 0, 100); UpdateWatchers(); ApplyAppearance();
            status.Text = "恢复完成；恢复前数据已自动保留在 Data 中。";
        }
        catch (Exception error) { Diagnostics.Log(store.DirectoryPath, error); if (!shuttingDown) status.Text = "恢复失败：" + error.Message; }
        finally { restoreWork = null; restoringLibrary = false; if (!shuttingDown && !IsDisposed) { main.Enabled = nav.Enabled = nowRail.Enabled = playerBar.Enabled = true; timer.Start(); } }
    }
    TextBox SettingsTextInput(string value) => new() { Text = value, BorderStyle = BorderStyle.FixedSingle, BackColor = UiTheme.Elevated, ForeColor = UiTheme.Ink, Font = Font };
    void SetAppearance(string value)
    {
        if (value is not ("dark" or "light")) return;
        store.State.Theme = value;
        store.SaveState();
        ApplyAppearanceCore(rebuild: page != "设置");
    }
    void ApplyAppearance() => ApplyAppearanceCore(rebuild: true);
    void ApplyAppearanceCore(bool rebuild)
    {
        bool previousLight = UiTheme.IsLight;
        UiTheme.SetLight(store.State.Theme == "light");
        UiTheme.RefreshControlColors(this, previousLight);
        if (!Contains(queue)) UiTheme.RefreshControlColors(queue, previousLight);
        if (!Contains(stage)) UiTheme.RefreshControlColors(stage, previousLight);
        UiTheme.WindowChrome(this, !immersiveVideo);
        // Recreate only the browsing controls. The playback engine, its video
        // handle and both media sessions remain alive during theme changes.
        railQueueDirty = true;
        if (rebuild) Render();
        else
            foreach (var choice in settingsThemeChoices)
                if (!choice.Button.IsDisposed) choice.Button.Selected = store.State.Theme == choice.Value;
        UpdatePlayerChrome(); Invalidate(true);
    }
    void EditLanguagePreferences()
    {
        using var dialog = new SettingsDialog("首选语言", "用逗号分隔语言代码，例如 zh, eng；越靠前优先级越高。", new Size(600, 360), dpiFactor, Font);
        var subtitles = SettingsTextInput(store.State.SubtitleLanguages); subtitles.AccessibleName = "字幕首选语言";
        var audio = SettingsTextInput(store.State.AudioLanguages); audio.AccessibleName = "音轨首选语言";
        AddDialogField(dialog.Body, "字幕", subtitles, 0); AddDialogField(dialog.Body, "音轨", audio, 78);
        dialog.Shown += (_, _) => { subtitles.Focus(); subtitles.SelectAll(); };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        static string Normalize(string value) => string.Join(',', value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        store.State.SubtitleLanguages = Normalize(subtitles.Text); store.State.AudioLanguages = Normalize(audio.Text);
        player.SetEngineOption("slang", store.State.SubtitleLanguages); player.SetEngineOption("alang", store.State.AudioLanguages); store.SaveState();
    }
    void AddDialogField(Panel host, string title, Control field, int top)
    {
        int P(float value) => (int)(value * dpiFactor);
        var label = new Label { Text = title, ForeColor = UiTheme.Muted, Font = Font, AutoSize = false };
        host.Controls.Add(label); host.Controls.Add(field);
        void Arrange() { label.SetBounds(0, P(top), host.Width, P(26)); field.SetBounds(0, P(top + 30), host.Width, P(32)); }
        host.Resize += (_, _) => Arrange(); Arrange();
    }
    void EditShortcutPreferences()
    {
        using var dialog = new SettingsDialog("键盘快捷键", "点击右侧输入框，再按下需要的按键或组合键。", new Size(600, 530), dpiFactor, Font);
        var inputs = new Dictionary<string, ShortcutCapture>(); int index = 0;
        foreach (var entry in store.State.Shortcuts)
        {
            var input = new ShortcutCapture(Enum.TryParse<Keys>(entry.Value, out var key) ? key : Keys.None) { AccessibleName = entry.Key };
            var label = new Label { Text = entry.Key, ForeColor = UiTheme.Ink, TextAlign = ContentAlignment.MiddleLeft, Font = Font };
            int row = index++; dialog.Body.Controls.Add(label); dialog.Body.Controls.Add(input); inputs.Add(entry.Key, input);
            void Arrange() { int y = (int)(row * 46 * dpiFactor); label.SetBounds(0, y, (int)(185 * dpiFactor), (int)(34 * dpiFactor)); input.SetBounds((int)(200 * dpiFactor), y, Math.Max(80, dialog.Body.Width - (int)(200 * dpiFactor)), (int)(34 * dpiFactor)); }
            dialog.Body.Resize += (_, _) => Arrange(); Arrange();
        }
        var error = new Label { ForeColor = UiTheme.Warning, Font = Font, Dock = DockStyle.Bottom, Height = (int)(32 * dpiFactor) }; dialog.Body.Controls.Add(error);
        dialog.SaveButton.DialogResult = DialogResult.None;
        dialog.SaveButton.Click += (_, _) =>
        {
            var keys = inputs.Values.Select(input => input.Binding).ToList();
            if (keys.Contains(Keys.None) || keys.Distinct().Count() != keys.Count) { error.Text = "每项都需要有效且不重复的快捷键。"; return; }
            dialog.DialogResult = DialogResult.OK; dialog.Close();
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        store.State.Shortcuts = inputs.ToDictionary(entry => entry.Key, entry => entry.Value.Binding.ToString()); store.SaveState();
    }
    string? ChooseSetting(string title, IEnumerable<Choice> options, string selected)
    {
        using var dialog = new SettingsDialog(title, "", new Size(640, 460), dpiFactor, Font, "确定");
        var list = new SettingsChoiceList { Dock = DockStyle.Fill, Font = Font, AccessibleName = title };
        dialog.Body.Controls.Add(list);
        foreach (var option in options) { list.Items.Add(option); if (option.Id == selected) list.SelectedItem = option; }
        dialog.SaveButton.Enabled = list.SelectedIndex >= 0;
        list.SelectedIndexChanged += (_, _) => dialog.SaveButton.Enabled = list.SelectedIndex >= 0;
        list.DoubleClick += (_, _) => { if (list.SelectedItem != null) { dialog.DialogResult = DialogResult.OK; dialog.Close(); } };
        dialog.Shown += (_, _) => list.Focus();
        return dialog.ShowDialog(this) == DialogResult.OK ? (list.SelectedItem as Choice)?.Id : null;
    }
    string? PromptSetting(string description, string value)
    {
        using var dialog = new SettingsDialog("编辑", "", new Size(620, 350), dpiFactor, Font, "确定");
        var instructions = new TextBox { Text = description, ReadOnly = true, TabStop = false, Multiline = true, BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = UiTheme.Surface, ForeColor = UiTheme.Muted, Font = Font };
        var input = SettingsTextInput(value); input.Dock = DockStyle.Bottom;
        dialog.Body.Controls.Add(instructions); dialog.Body.Controls.Add(input);
        dialog.Shown += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog(this) == DialogResult.OK ? input.Text : null;
    }
}
