# 原生播放运行库、对应源码与重链接

应用使用 libmpv，未自行重写解码器。Git 仓库只提供应用与自有 helper 的源代码，不携带 libmpv、SDK、NuGet 包、静态归档、安装器或个人维护备份。

## 使用外部运行库

参考 [mpv 安装页](https://mpv.io/installation/) 和 [mpv 源码](https://github.com/mpv-player/mpv)。Windows 构建的提供者、构建选项与许可可能不同；不要将所有下载包统称为 LGPL，也不要把未测试的新内核当作本版已验证内核。

用户可以向构建脚本传入自备 `libmpv-2.dll`，输出中保留文件原名。没有禁止替换、调试或重新链接第三方库的条款。

## 正式 1.0.5 的固定运行库

正式安装包沿用已实测的 R3 DLL：67,378,641 字节，SHA256 为 `649375F4CBE1BD0455F00C8CBCB6BA7EE85F431DE6432202D53A30F0C7F99F19`。其自有预热 helper 源码在仓库，正式 helper SHA256 为 `0D51D01299885E7CEEBF82988F18A0871D6F76DD5F5EB65142A0F60994C282E9`。仓库不保存二进制，安装包和大体积依赖材料放在 [v1.0.5 Release](https://github.com/fengyuan0601-prog/HARMOVUE/releases/tag/v1.0.5)。

主要上游修订：

| 项目 | 修订 |
| --- | --- |
| mpv | `fa0534a729dd3dff33ea03cc7c226d317a4fcd07` |
| FFmpeg | `179d08349347e00b2a24593cfa8b3cbbde2a6cfb` |
| libplacebo | `1937beef3a2f508266c68efea1491e3fc4600e04` |

该本地构建曾对三者采用大小优化，并复用固定的其他输入；FFmpeg 的 AC3 构建清单补入其原有 `audio_frame_queue.o`。其 FFmpeg 配置报告 LGPL v3 or later，不能仅用 mpv 的 LGPL-2.1 文本概括全部静态组成。

## 同页分发材料

- `HARMOVUE-1.0.5-win-x64-Setup.exe`：自包含 Windows x64 安装包。
- `HARMOVUE-1.0.5-native-source.zip`：完整固定源码归档、修改、配置、实际静态输入、Cargo 锁/源码缓存、原始及参数化构建入口、工具身份和声明。
- `HARMOVUE-1.0.5-managed-dependency-source.zip`：TagLibSharp 2.3.0 对应源码、固定 Pinyin 源码与许可。
- `HARMOVUE-1.0.5-validation.zip`、`SHA256SUMS.txt`：本次实际验证结果、范围与资产校验。

材料免费提供，无需先联系维护者。最终成员索引包含 98 个静态、系统导入和运行库归档，不是 98 个独立运行库。成员、来源与许可选择见 [DEPENDENCIES.json](../DEPENDENCIES.json) 及材料内 `manifest/RUNTIME_DEPENDENCIES.json`。配置检查使用、却没有成员保留到正式 DLL 的库单列为构建输入；LTO 选择不被冒充为逐函数分配证明。

FreeType 选择 FTL，Highway 选择 BSD-3-Clause；MPL 和 LCEVCdec 的原许可不由应用 GPL 重新授权。GCC 保留 Runtime Library Exception，Rust 标准库的完整第三方清单另附。源文件声明汇编中额外的工具、测试和可选部件不一概算作运行组成。[OPEN_SOURCE.html](../OPEN_SOURCE.html) 为安装版的离线许可入口。

原开发层固定为 `ghcr.io/btbn/ffmpeg-builds/win64-lgpl@sha256:59662fba1b2d870e1aa1d074d3620b71dc98c62df01e2682a0edec224658ad7a`，BtbN 配方修订 `63ac3265b0789ea16c7b1f8351cd2ad195a28722`。公开材料剔除其中无关的库，记录原层与精简层各自摘要。MSYS2 的官方 `.BUILDINFO`、配方和实际静态归档身份逐项核对；bzip2 和所用 MinGW-w64 的完整源码一并保留。各项具体证据在原生材料的 `provenance/` 和 `manifest/`。

## 重建与替换

准备材料所记录的 GCC 16.2.0-4 / MSYS2 通用工具链，路径可通过 `ToolchainRoot` 指定；没有把另一项目的私有安装复制为分发前提。脚本校验工具身份、拒绝非空目标、不改系统配置或原媒体。在新的受控 E 盘 ASCII 路径执行：

```powershell
.\scripts\Restore-R3.ps1 -Root E:\HarmovueBuild `
  -ToolchainRoot E:\Tools\msys64\ucrt64
E:\HarmovueBuild\tools\Rebuild-Addons.ps1
E:\HarmovueBuild\tools\Build-R3.ps1
```

恢复与编译是分开的步骤。Cargo 锁定并离线运行，Meson 禁用网络 fallback；不隐式下载新版本。入口重建 LuaJIT/Subrandr、libplacebo、FFmpeg 和 mpv，其余库提供固定静态输入、对应源码、嵌套归档及上游配方。通用工具链为明确外部前提；没有宣称全部上游库均重新从源码编译。新路径和环境可能改变摘要，不承诺位级可重复。

退出程序后，可将 ABI 兼容并经验证的新库以 `libmpv-2.dll` 原名替换，或传给[应用构建入口](BUILD.md)。没有锁定设备的启动验证、阻止 LGPL 修改测试或合法调试/重新链接的条款。TagLibSharp 的单文件应用重发布方法也见构建文档。

## 实际验证范围

各项执行、摘要和通过状态以 Release 验收记录及材料内 `CURRENT_VALIDATION.json` 为准。重建、ABI、解码、设备 PCM、可见画面、冷启动、安装和 Data 保留分别记录，不相互替代。独立重链接不等于新路径产物与正式 R3 字节相同，也不等于任意设备和格式均已实机验证。

携带运行库发布时，应提供与实际二进制对应的源码、修改、构建配置及所需材料，保留上游声明，并按各组件的实际许可核对。参见 [FFmpeg 官方许可与分发说明](https://ffmpeg.org/legal.html) 和运行库提供者的材料。这是一份具体的发布状态记录，不宣称已经完成全部第三方分发审查。

v1.0.4-source 保留为当时的仅源码首发，不把那时尚未完成的分发工作追记为已通过。1.0.5 以自己的真实载荷和记录为准。
