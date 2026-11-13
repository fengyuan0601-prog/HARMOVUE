// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

namespace LocalMedia;

static class Brand
{
    static Stream Resource(string name) => typeof(Brand).Assembly.GetManifestResourceStream(name)
        ?? throw new InvalidDataException("品牌资源缺失：" + name);
    public static Icon AppIcon() { using var stream = Resource("Brand.Icon"); using var icon = new Icon(stream); return (Icon)icon.Clone(); }
    public static Image Mark() { using var stream = Resource("Brand.Mark"); using var image = Image.FromStream(stream); return new Bitmap(image); }
    public static Image Logo() { using var stream = Resource("Brand.Logo"); using var image = Image.FromStream(stream); return new Bitmap(image); }
}
