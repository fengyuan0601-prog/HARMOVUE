// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.Security.Cryptography;
using System.Text;

namespace LocalMedia;

public static class Subtitles
{
    public static string NormalizeLanguage(string language) => language.ToLowerInvariant().Split('-')[0] switch { "chi" or "zho" or "chs" or "cht" => "zh", "eng" => "en", "jpn" => "ja", "kor" => "ko", "fra" => "fr", "deu" => "de", var value => value };
    public static string Language(string mediaPath, string subtitlePath)
    {
        string stem = Path.GetFileNameWithoutExtension(mediaPath), name = Path.GetFileNameWithoutExtension(subtitlePath);
        if (!name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)) return "";
        string suffix = name[(stem.Length + 1)..].Split('.')[0].ToLowerInvariant();
        return NormalizeLanguage(suffix) is "zh" or "en" or "ja" or "ko" or "fr" or "de" or "es" or "ru" or "it" ? suffix : "";
    }
    static readonly HashSet<string> text = new([".srt", ".ass", ".ssa", ".vtt"], StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> supported = new([".srt", ".ass", ".ssa", ".vtt", ".sup", ".idx", ".sub", ".mks"], StringComparer.OrdinalIgnoreCase);
    public static IEnumerable<string> Discover(Media media)
    {
        string name = Path.GetFileNameWithoutExtension(media.Path);
        var paths = Directory.Exists(media.Folder) ? Directory.EnumerateFiles(media.Folder).Where(p => supported.Contains(Path.GetExtension(p)) && (Path.GetFileNameWithoutExtension(p).Equals(name, StringComparison.OrdinalIgnoreCase) || Path.GetFileNameWithoutExtension(p).StartsWith(name + ".", StringComparison.OrdinalIgnoreCase))) : [];
        return paths.Concat(media.ExternalSubtitles).Where(File.Exists).Where(p => !Path.GetExtension(p).Equals(".sub", StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.ChangeExtension(p, ".idx"))).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static string Prepare(string source, string codepage, string cache)
    {
        string result = Prepare(source, codepage, cache, out var lease);
        lease.Dispose();
        return result;
    }
    sealed class SourceLease : IDisposable { public void Dispose() { } }
    // The playback owner keeps this lease until the track/subtitles are removed.
    // Cache pressure or an explicit clear cannot delete a loaded converted file.
    public static string Prepare(string source, string codepage, string cache, out IDisposable lease)
    {
        lease = new SourceLease();
        if (!text.Contains(Path.GetExtension(source)) || codepage == "auto") return source;
        if (new FileInfo(source).Length > 20 * 1024 * 1024) throw new IOException("字幕文件超过 20 MiB，请检查是否选错文件；也可选择自动编码直接交给播放内核读取。");
        byte[] bytes = File.ReadAllBytes(source);
        string value;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }) || bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }) || bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff }))
        {
            using var stream = new MemoryStream(bytes);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
            value = reader.ReadToEnd();
        }
        else
        {
            try { _ = new UTF8Encoding(false, true).GetString(bytes); return source; }
            catch (DecoderFallbackException) { }
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var encoding = Encoding.GetEncoding(codepage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            value = encoding.GetString(bytes);
        }
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(source) + "|" + File.GetLastWriteTimeUtc(source).Ticks + "|" + bytes.Length + "|" + codepage)));
        string dest = Path.Combine(cache, "sub-" + key + Path.GetExtension(source));
        if (!MediaCache.TryWritePinned(cache, dest, new UTF8Encoding(false).GetBytes(value), out lease))
            throw new IOException("播放器缓存空间正被播放中的文件使用。请停止播放或清理缓存后重新加载字幕。");
        return dest;
    }
}
