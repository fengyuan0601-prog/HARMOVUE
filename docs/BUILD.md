# 构建

需要 Windows x64、global.json 指定的 .NET SDK 10.0.402，以及 PowerShell 7。脚本不自动安装工具，也不更改系统安全策略。本地建议将源码、SDK、缓存、输出与合成测试数据放在 E 盘；默认缓存和输出放在仓库的 `.cache`、`artifacts`，可显式调整。

## 无播放内核的源码构建

```powershell
.\tools\build.ps1 -SourceOnly
.\tools\test.ps1 -Application .\artifacts\app\LocalMedia.exe
```

这用于验证托管代码、拼音搜索、字幕文本处理、文件引用、歌单和 SQLite 备份/恢复等数据逻辑。`SourceOnly` 明确排除本地可能存在的 libmpv 和预热 helper。该产物不能用于验收真实播放，CI 不上传其可执行文件或安装包。

直接用 `dotnet build src/LocalMedia.csproj -c Release` 也可编译应用代码；原生依赖为运行时输入。

## 可播放构建

从 [mpv 的安装页](https://mpv.io/installation/) 查找上游说明和构建来源。选取兼容的 Windows x64 libmpv，并核对来源、版本、SHA256 与随附许可；本项目不自动选择某个最新二进制。

```powershell
.\tools\build.ps1 -NativeLibrary 'E:\deps\libmpv-2.dll' `
  -ExpectedNativeSHA256 '<该文件经核对的SHA256>' `
  -OutputDirectory 'E:\build\HARMOVUE'
```

将占位 SHA 替换为实际值；也可不传 `ExpectedNativeSHA256`，脚本会记录实际摘要，但不会把“记录了摘要”当作来源验证。DLL 不必放进 Git 仓库。播放内核必须是 AMD64；保留 DLL 替换能力，不使用自制解码器。

新输出目录不可已有文件，特别不可含个人 `Data`。脚本不会清空目录，也不会覆盖现有资料库。输出的 `BUILD_PROVENANCE.json` 记录是否仅源码构建、实际 SDK、原生摘要和 helper 状态；它不是公开分发许可的放行记录。

## 可选预热辅助程序

提供已有的 x64 MinGW-w64 GCC：

```powershell
.\tools\build.ps1 -NativeLibrary 'E:\deps\libmpv-2.dll' `
  -HelperCompiler 'E:\tools\mingw64\bin\gcc.exe' `
  -OutputDirectory 'E:\build\HARMOVUE'
```

helper 源码为 `tools/native-prewarm/native-prewarm.c`，只使用 Windows 系统 API，不含解码器。缺失时应用回退到正常加载路径；不能把原有优化内核与 helper 组合的历史耗时写成任意构建的保证。

## 缓存和工具路径

```powershell
.\tools\build.ps1 -SourceOnly `
  -DotNet 'E:\tools\dotnet\dotnet.exe' `
  -CacheRoot 'E:\cache\HARMOVUE' `
  -PackageCache 'E:\cache\nuget' `
  -OutputDirectory 'E:\build\HARMOVUE-source-check'
```

NuGet 使用锁文件。首次恢复需要访问包源，已有离线包源的维护者可按 .NET 标准还原方式使用该源；不要修改锁文件来掩盖恢复失败。

## 安装器

`tools/installer.nsi` 与相关 NSIS 文件保留安装路径、文件占用预检、相同原生库保留、旧布局迁移和 Data 保护逻辑。使用 `tools/Compile-Installer.ps1` 生成与准确载荷相符的预检/卸载清单，不直接绕过脚本调用旧 NSIS 文件。需另备 NSIS，并显式传入工具路径。

本仓库的 CI 不构建或分发安装包。发布携带第三方运行库的二进制之前，维护者需要完成[原生分发材料](NATIVE_RUNTIME.md)中列出的工作；本地成功构建不能代替这些工作。
