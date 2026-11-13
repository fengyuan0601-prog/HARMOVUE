// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

namespace LocalMedia;

public sealed partial class MainWindow
{
    void BuildCollectionsPage()
    {
        var actions = Actions();
        actions.Controls.Add(Button("新建音乐歌单", () => NewList(false), 140, 36));
        actions.Controls.Add(Button("新建视频集合", () => NewList(true), 140, 36));
        actions.Controls.Add(Button("导入 M3U / M3U8", ImportList, 164, 36));

        var body = new GlassCanvas { Dock = DockStyle.Fill, AutoScroll = true };
        var sections = new List<(Control Control, int Height, int Gap)>();
        var rows = new List<(AlbumTilePanel Row, Label Title, Label Detail, UiButton Open)>();
        void Section(bool videoList)
        {
            var lists = store.State.Lists.Where(list => list.Video == videoList).ToArray();
            string kind = videoList ? "视频集合" : "音乐歌单";
            var heading = Caption(kind + (lists.Length > 0 ? "  ·  " + lists.Length : ""), 12, bold: true);
            body.Controls.Add(heading); sections.Add((heading, 32, 8));
            if (lists.Length == 0)
            {
                var empty = Caption(videoList ? "暂无集合" : "暂无歌单", 10, UiTheme.Muted);
                body.Controls.Add(empty); sections.Add((empty, 32, 16));
                return;
            }
            foreach (var list in lists)
            {
                var row = new AlbumTilePanel
                {
                    Height = 70, Cursor = Cursors.Hand, AccessibleName = list.Name + "，" + list.Items.Count + (videoList ? " 部视频" : " 首歌曲"),
                    AccessibleDescription = "按 Enter 查看" + kind
                };
                var title = Caption(list.Name, 11); title.Cursor = Cursors.Hand;
                var detail = Caption(list.Items.Count + (videoList ? " 部视频" : " 首歌曲"), 9, UiTheme.Muted); detail.Cursor = Cursors.Hand;
                void Open() => Try(() => { EditList(list); if (!shuttingDown && page == "歌单与集合") Render(); });
                var open = IconButton("查看" + kind + "：" + list.Name, "\uE76C", Open);
                open.TabStop = false;
                foreach (var target in new Control[] { row, title, detail }) target.Click += (_, _) => Open();
                row.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { Open(); e.Handled = true; e.SuppressKeyPress = true; } };
                row.GotFocus += (_, _) => row.Invalidate(); row.LostFocus += (_, _) => row.Invalidate();
                row.Paint += (_, e) =>
                {
                    float scale = row.DeviceDpi / 96f;
                    UiTheme.Glyph(e.Graphics, videoList ? UiTheme.Video : UiTheme.Music, new Rectangle(0, 0, (int)(36 * scale), row.Height), UiTheme.Muted, 15);
                    if (row.Focused)
                        ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(row.ClientRectangle, -1, -1), UiTheme.Ink, row.BackColor);
                };
                row.Controls.AddRange([title, detail, open]); body.Controls.Add(row);
                rows.Add((row, title, detail, open)); sections.Add((row, 70, 4));
            }
            var spacer = new GlassCanvas(); body.Controls.Add(spacer); sections.Add((spacer, 16, 0));
        }
        Section(false); Section(true);

        bool arranging = false;
        void Arrange()
        {
            if (arranging || body.IsDisposed || dpiReady && !dpiScaled.Contains(body)) return;
            arranging = true;
            var previousScroll = body.AutoScrollPosition;
            body.SuspendLayout();
            try
            {
                body.AutoScroll = false; body.AutoScrollMinSize = Size.Empty;
                float scale = dpiReady ? dpiFactor : 1; int P(float value) => (int)(value * scale);
                int width = Math.Max(P(120), Math.Min(P(820), body.ClientSize.Width - P(20)));
                int y = P(12);
                foreach (var section in sections)
                {
                    section.Control.SetBounds(0, y, width, P(section.Height));
                    y += P(section.Height + section.Gap);
                }
                foreach (var row in rows)
                {
                    row.Title.SetBounds(P(50), P(11), Math.Max(P(40), width - P(102)), P(26));
                    row.Detail.SetBounds(P(50), P(38), Math.Max(P(40), width - P(102)), P(22));
                    row.Open.SetBounds(width - P(36), P(18), P(32), P(32));
                }
                body.AutoScrollMinSize = new Size(0, y);
            }
            finally
            {
                body.ResumeLayout(); body.AutoScroll = true;
                body.AutoScrollPosition = new Point(-previousScroll.X, -previousScroll.Y);
                arranging = false;
            }
        }
        body.SizeChanged += (_, _) => Arrange();
        content.Controls.Add(body); body.BringToFront();
        if (IsHandleCreated) BeginInvoke((Action)Arrange);
    }

    void BuildQueuePage()
    {
        var header = new GlassCanvas { Dock = DockStyle.Top, Height = 44 };
        var heading = Caption("", 13, bold: true);
        header.Controls.Add(heading);
        var empty = new GlassCanvas { Dock = DockStyle.Fill };
        var emptyText = Caption("队列为空", 11, UiTheme.Muted);
        emptyText.Dock = DockStyle.Fill; emptyText.TextAlign = ContentAlignment.MiddleCenter;
        empty.Controls.Add(emptyText);
        // Keep the shared queue directly under content: wrapping it in a new
        // container would make Render scale this existing control a second time.
        content.Controls.Add(header); content.Controls.Add(empty); content.Controls.Add(queue);
        queue.BringToFront();
        void UpdateHeading()
        {
            if (header.IsDisposed) return;
            int count = player.Session.Queue.Count;
            heading.Text = (store.State.Active == "video" ? "视频队列" : "音乐队列") + "  ·  " + count + " 项";
            queue.Visible = count > 0; empty.Visible = count == 0;
        }
        void Arrange()
        {
            if (header.IsDisposed || dpiReady && !dpiScaled.Contains(header)) return;
            float scale = dpiReady ? dpiFactor : 1; int P(float value) => (int)(value * scale);
            heading.SetBounds(0, P(6), header.Width, P(30));
        }
        EventHandler selectionChanged = (_, _) => UpdateHeading();
        KeyEventHandler keyDown = (_, e) => { if (e.KeyCode == Keys.Enter) { QueuePlay(queue, EventArgs.Empty); e.Handled = true; e.SuppressKeyPress = true; } };
        queue.SelectedIndexChanged += selectionChanged; queue.KeyDown += keyDown;
        header.Disposed += (_, _) => { queue.SelectedIndexChanged -= selectionChanged; queue.KeyDown -= keyDown; queue.Visible = true; };
        header.SizeChanged += (_, _) => Arrange();
        RefreshQueue(); UpdateHeading();
        if (IsHandleCreated) BeginInvoke((Action)Arrange);
    }
}
