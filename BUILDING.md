# 构建当前 Native 版本

当前工程在 `native/`，使用 C# / WinUI 3、Rust Core 桥接和 Inno Setup。根目录不再提供原 Tauri / React 构建入口。

## 构建环境

- Windows x64。
- .NET 10 SDK，项目目标及包版本以 `native/src/OpenRevo.Native/OpenRevo.Native.csproj` 为准。
- Rust Cargo，MSVC x64 工具链。
- Visual Studio C++ Build Tools，包含 x64/x86 MSVC 与 Windows SDK。安装器目录筛选 DLL 使用 x86 C++ 编译。
- Inno Setup 的 `ISCC.exe`。
- 首次还原需要访问 NuGet 与 Cargo 软件源。

## 从仓库根目录打包

下面以 0.6.9 和常见 Inno Setup 路径举例；按本机实际位置设置 `-Iscc`，发布号必须与项目配置一致。

```powershell
& .\native\build.ps1 `
  -Iscc 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' `
  -Compact `
  -Variant shared `
  -OutputName Open-Revo-0.6.9-Setup-x64
```

`-DotNet` 与 `-Cargo` 可指定工具位置。仅在依赖已还原且缓存完整时使用 `-Offline`。保留锁文件，不以重新解析依赖代替故障定位。

脚本会发布 C# 主程序，构建 Rust 桥接、安装器目录辅助 DLL，归档完整对应源码，再生成安装器。运行目录和源码归档位于 `native/` 下，安装包输出到仓库根目录。默认共享 .NET，不捆绑整套 .NET 运行时；保留 Windows App Runtime 及未来网页插件兼容所需库。

`-Compact` 是安装器压缩选项，不是 .NET 裁剪。不要恢复历史自包含/AOT 实验参数。

## 构建、检查与实机操作分开

普通构建不启动程序、不操作硬件。`-RunChecks` 是显式选择的已有检查入口；运行界面、硬件读写、安装升级及其他检查须遵守本次任务授权。仅打包不等于授权运行它们。

本次仓库整理没有修改 `native/` 中任何文件，因此继续使用已验证的同一构建入口和依赖；没有因目录整理新增构建或硬件测试结论。

## 发布

见 [维护说明](MAINTENANCE.md) 和 [0.6.9 更新日志](docs/releases/0.6.9.md)。发布只包含安装包及完整 GPL 对应源码，使用 `native-v` 标签和用户指定的公开版本。内部测试代号不自动推进发布号。

原 Tauri 的 Node/pnpm、NSIS、Slint 与 Gitee 镜像流程仅保存在[旧工程归档](https://github.com/csucm/open-revo-winUI3/tree/archive-tauri-web-20261107)，不是当前 Native 的发布入口。
