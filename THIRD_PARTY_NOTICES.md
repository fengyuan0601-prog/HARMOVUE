# 第三方来源与许可

HARMOVUE 自有源代码按 GPL-3.0-or-later 发布。以下组件保留自己的版权及许可；根目录 LICENSE 不重新授权它们。`licenses/` 与 `native/LICENSE.LGPL` 保存已有许可文本，具体二进制还应保留其提供者随附的声明。

| 组件 | 版本或来源 | 许可记录 |
| --- | --- | --- |
| .NET / Windows Desktop | SDK 10.0.402，对应运行时 10.0.13 | .NET 开源部分 MIT；分发文件和其他组成见 `dotnet-LICENSE.txt`、`dotnet-ThirdPartyNotices.txt` |
| Microsoft.Data.Sqlite / Core | NuGet 10.0.13 | MIT，上游 dotnet/efcore |
| SQLitePCLRaw bundle/config/core/provider | NuGet 3.0.5 | Apache-2.0 |
| SQLite | 由锁定 NuGet 依赖提供 | Public Domain；以实际包中内容和声明为准 |
| TagLibSharp | NuGet 2.3.0；mono/taglib-sharp | LGPL-2.1-only，见 `TagLibSharp-LGPL.txt`；[对应上游标签](https://github.com/mono/taglib-sharp/tree/TaglibSharp-2.3.0.0) |
| ToolGood.Words.Pinyin | NuGet 3.1.0.4；修订 `de86cad8127ee9706b1d4c47d57d63ee4f65155c` | MIT，见同名文本；[上游源码](https://github.com/toolgood/ToolGood.Words.Pinyin/tree/de86cad8127ee9706b1d4c47d57d63ee4f65155c) |
| Windows SDK .NET 投影 / WinRT | 由 .NET SDK 与锁文件解析 | 相关 MIT/CsWinRT 声明随 `licenses/` 保存 |
| libmpv / FFmpeg 及其组成 | 正式 1.0.5 使用 R3 内核，SHA256 `649375F4CBE1BD0455F00C8CBCB6BA7EE85F431DE6432202D53A30F0C7F99F19` | FFmpeg 本次配置 LGPL-3.0-or-later；全部实际静态组成及多许可选择见 `DEPENDENCIES.json` 与 `OPEN_SOURCE.html` |
| NSIS | 3.04，安装器使用 zlib stub | `licenses/NSIS-COPYING.txt`；未使用 CPL 的 LZMA 压缩模块 |

锁定的直接/传递 NuGet 版本见 `src/packages.lock.json`。第三方许可中的作者联系方式属于上游原文，保留原文不表示这些作者支持本项目。

品牌图形由项目提供者提供并纳入此次开源授权；其版权使用按根目录 GPL-3.0-or-later，商标及身份说明见 [TRADEMARKS.md](TRADEMARKS.md)。没有嵌入音乐、视频、个人数据库、第三方字体包或其他项目的数据。

本应用不禁止合法调试、替换或重新链接 LGPL 组件。安装目录的 `OPEN_SOURCE.html` 提供可离线阅读的许可索引；`licenses/native/THIRD_PARTY_TEXTS.txt` 与 `SOURCE_FILE_NOTICES.txt` 保留原文，后者包含额外源码/测试/构建上下文，不表示所有文件均链接到 DLL。

对应源码、构建修改、配置、固定输入和重链接方法与安装包在[同一版本 Release](https://github.com/fengyuan0601-prog/HARMOVUE/releases/tag/v1.0.5)免费提供。TagLibSharp 打包进单文件应用，使用其修改版时可从完整应用源码重新发布；没有阻止 LGPL 修改验证的附加条款。第三方组件不因应用使用 GPL 而失去其原有许可。

This software is based in part on the work of the Independent JPEG Group. FFmpeg 的 `jfdctfst.c`、`jfdctint_template.c` 和 `jrevdct.c` 未修改；唯一的 FFmpeg 源树修改是在 AC3 encoder 的 Makefile 对象列表中补入其原有 `audio_frame_queue.o`。

Portions of this software are copyright © 1996–2026 The FreeType Project (https://freetype.org). All rights reserved. FreeType 选择 FTL；Highway 选择 BSD-3-Clause；可多选的 MIT/Apache crate 优先保留并使用 MIT 选项，其余按实际文件文本处理。Subrandr 的 MPL-2.0 源码保持原许可；LCEVCdec 保持 BSD-3-Clause-Clear 与 V-Nova 额外声明；GCC 运行库保留 Runtime Library Exception。
