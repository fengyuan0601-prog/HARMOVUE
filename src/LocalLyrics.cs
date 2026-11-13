// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalMedia;

internal sealed record LyricsContent(string Text, List<(double Time, string Text)> Lines);
internal static class LocalLyrics
{
    const int MaximumBytes = 2 * 1024 * 1024;
    static readonly SemaphoreSlim slots = new(2, 2);
    static readonly Regex stamp = new(@"\[(\d{1,5}):(\d{1,2}(?:\.\d{1,5})?)\]", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    public static async Task<LyricsContent> ReadAsync(string mediaPath, CancellationToken cancel)
    {
        var work = Task.Run(async () =>
        {
            await slots.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                cancel.ThrowIfCancellationRequested();
                string path = Path.ChangeExtension(mediaPath, ".lrc");
                if (!File.Exists(path)) path = Path.ChangeExtension(mediaPath, ".txt");
                if (!File.Exists(path)) return new LyricsContent("没有本地歌词", []);
                using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (source.Length > MaximumBytes) throw new IOException("歌词文件超过 2 MiB，无法显示。");
                using var bytes = new MemoryStream();
                var buffer = new byte[16384];
                int count;
                while ((count = await source.ReadAsync(buffer, cancel).ConfigureAwait(false)) != 0)
                {
                    if (bytes.Length + count > MaximumBytes) throw new IOException("歌词文件超过 2 MiB，无法显示。");
                    bytes.Write(buffer, 0, count);
                }
                byte[] raw = bytes.ToArray(); string text;
                if (raw.Length >= 2 && (raw[0] == 0xff && raw[1] == 0xfe || raw[0] == 0xfe && raw[1] == 0xff))
                    text = (raw[0] == 0xff ? Encoding.Unicode : Encoding.BigEndianUnicode).GetString(raw, 2, raw.Length - 2);
                else
                    try { text = new UTF8Encoding(false, true).GetString(raw).TrimStart('\uFEFF'); }
                    catch (DecoderFallbackException) { text = Encoding.GetEncoding(936).GetString(raw); }
                var lines = new List<(double Time, string Text)>();
                foreach (string line in text.Split('\n'))
                {
                    cancel.ThrowIfCancellationRequested();
                    string caption = stamp.Replace(line, "").Trim();
                    foreach (Match match in stamp.Matches(line))
                        if (int.TryParse(match.Groups[1].Value, out int minutes) && double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
                            lines.Add((minutes * 60 + seconds, caption));
                }
                return new LyricsContent(text, lines.OrderBy(l => l.Time).ToList());
            }
            finally { slots.Release(); }
        }, cancel);
        return await work.WaitAsync(cancel).ConfigureAwait(false);
    }
}
