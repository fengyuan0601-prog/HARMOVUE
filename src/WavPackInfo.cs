// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

using System.Buffers.Binary;

namespace LocalMedia;

public static class WavPackInfo
{
    // WavPack 5.9.0 include/wavpack.h: flags at byte 24; HYBRID_FLAG = 8.
    // Describes encoding mode, not file integrity or physical device output.
    public static string Describe(string path)
    {
        if (!Path.GetExtension(path).Equals(".wv", StringComparison.OrdinalIgnoreCase)) return "";
        try
        {
            Span<byte> header = stackalloc byte[32];
            using var file = File.OpenRead(path);
            if (file.Read(header) != header.Length || !header[..4].SequenceEqual("wvpk"u8)) return "WavPack格式信息未知";
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(header[24..28]);
            if ((flags & 8) == 0) return "WavPack纯无损编码（未校验文件完整性）";
            return "WavPack混合有损基础流；" + (File.Exists(Path.ChangeExtension(path, ".wvc")) ? "发现校正文件，但当前内核未应用，不能按完整无损播放" : "未发现同名校正文件，按有损流播放");
        }
        catch (IOException) { return "WavPack格式信息暂不可用"; }
        catch (UnauthorizedAccessException) { return "WavPack格式信息暂不可用"; }
    }
}
