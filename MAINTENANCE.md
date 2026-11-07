# Native 维护与旧工程归档

2026-11-07，用户授权整理仓库，并要求不影响功能和后续维护。

## 当前入口

| 目录/文件 | 职责 |
| --- | --- |
| `native/src/OpenRevo.Native/` | 当前 WinUI 界面、mini、托盘、OSD 与交互 |
| `native/src/OpenRevo.Core/` | 当前 C# 系统服务、硬件队列、配置和诊断 |
| `native/src/OpenRevo.CoreBridge/` | 当前 Rust C ABI 桥接 |
| `native/src/OpenRevo.Upstream/` | 固定上游 Core、资源、许可与已适配的本地补丁 |
| `native/src/OpenRevo.Checks/` | 现有定向检查 |
| `native/src/Branding/` | 正式图标与核对脚本 |
| `native/build.ps1`、`native/installer.iss` | 当前构建、对应源码归档与安装器 |
| `native/installer/` | 安装器目录选择辅助，当前仍使用 C++ |
| `docs/releases/` | 公开版本日志 |
| `docs/previews/` | 各历史版本的原生界面截图 |

构建依赖和命令见 [BUILDING.md](BUILDING.md)，协作规则见 [AGENTS.md](AGENTS.md)。`native/` 中部分说明保留历史版本或本地工作区路径，查当前入口时以本页和公开版本日志为准。

## 归档位置和范围

完整整理前状态保存在分支 [archive-tauri-web-20261107](https://github.com/csucm/open-revo-winUI3/tree/archive-tauri-web-20261107)，起点为 `f69b32709e5bdebed38c17c7f32ae12c39112733`。原 `native-v0.6.9` 及更早发布标签保留。

| 原主分支路径 | 后续查阅位置 |
| --- | --- |
| `src/`、根 `index.html` | 归档分支的原 React / TypeScript / CSS 界面 |
| `src-tauri/` | 归档分支的原 Tauri、Rust 后端、FFI 与 Slint OSD |
| `plugins_sample/` | 归档分支的网页插件样例和资源，供未来兼容工作参考 |
| `package.json`、pnpm/Vite/TS 配置、`.npmrc` | 归档分支的原网页构建工具链 |
| `latest.json` | 归档分支的旧 Tauri 更新清单；Native 更新查询 GitHub `native-v*` Release |
| `.github/workflows/release.yml` | 归档分支的旧 Tauri/NSIS 发布流程，主分支退役 |
| `.github/workflows/sync-to-gitee.yml` | 归档分支的原作者 Gitee 镜像流程，主分支退役；未访问或修改 Gitee |
| 原版协作说明、英文首页和开发手册 | 归档分支相同路径，保留原上下文 |

需要查阅时可直接浏览归档分支，或在独立工作目录检出它。不要把整个旧 Core 同步覆盖到 Native：两份 Core 有版本和本地适配差异。选择性移植时记录来源 SHA、改动及验证范围，并保留许可和原作者版权。

## 这次整理保护的内容

- `native/` 下全部文件原字节保留，包括硬件协议、机型表、锁文件、图标、资源、WebView2 兼容库配置和构建脚本。
- 当前 0.6.9 Release、附件和标签保留，不重新编译、重打包或替换已测试安装包。
- 旧工程从主分支归档，不改写 Git 历史，不删除归档分支或旧标签。
- 根 `.gitattributes` 只说明文档统计及保留活跃 Core 的可见性，不设置换行或编码转换，不把当前硬件代码伪标为生成文件。
- 此次维护整理不宣称安装包体积或运行内存下降，也不代表新增硬件功能已验收。

## 后续开发和发布

先定位实际日志和受影响路径，最小范围修复；不顺带重写已验收功能。硬件动作继续保留机型准入、供电和参数限制、串行执行、写前状态比较及回读。未知保持未知。

内部测试代号与公开号独立；公开版本由用户指定。发布时同步程序集、manifest、安装器与公开更新基准，不从内部号推算。软件仅显示公开号；发布标签沿用 `native-v<版本>`，安装包名为 `Open-Revo-<版本>-Setup-x64.exe`。

发布附件只包含安装器和 GPL 完整对应源码；上传前核对源码及载荷，上传后核对附件大小、摘要、下载地址。截图和说明放仓库文档，不另作 Release 下载附件。

当前没有启用新的 GitHub Actions Native 自动发布流程。经验证的构建入口仍是 `native/build.ps1`。未来如接入云构建，应单独验证 Windows SDK、C++ x86 辅助、共享运行时和源码对应检查，再启用；不要把旧 Tauri 流程改名后当作已验证的 Native CI。
