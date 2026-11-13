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
| libmpv / FFmpeg 及其组成 | 用户提供的运行时依赖，本仓库不含二进制 | 构建选项与静态组成影响实际许可；见[运行库来源与发布状态](docs/NATIVE_RUNTIME.md) |

锁定的直接/传递 NuGet 版本见 `src/packages.lock.json`。第三方许可中的作者联系方式属于上游原文，保留原文不表示这些作者支持本项目。

品牌图形由项目提供者提供并纳入此次开源授权；其版权使用按根目录 GPL-3.0-or-later，商标及身份说明见 [TRADEMARKS.md](TRADEMARKS.md)。没有嵌入音乐、视频、个人数据库、第三方字体包或其他项目的数据。

本应用不禁止合法调试、替换或重新链接 LGPL 组件。本次公开源码不构成旧原生安装包分发材料已经通过核对的声明。
