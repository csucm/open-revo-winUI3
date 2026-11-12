# OpenRevo Core 接入验证（2026-10-29）
## 0.9.4：调色修正（2026-11-01）

固定上游提交不变，0.9.4加入本地调色补丁。旧0.9.3包不含此修改。Magnification初始化、矩阵读取/写入和释放在同一按需线程执行；关闭时停止并join线程，再允许C#卸载DLL。C ABI查询22为只读矩阵诊断；查询16新增engine_error。服务动作7仍为唯一色彩写入入口，保留参数校验、原矩阵/Gamma基线及失败恢复。

本机实验发现初始化后换线程读取失败；当前矩阵原样写回返回Win32 87，微量偏移写入及恢复却成功。因此相同请求先读取实际矩阵，等价时跳过重复写入；不同请求写入后再回读，不能只凭API返回成功判定生效。浮点比较只容忍1e-5误差并拒绝非有限值。公式与预设来源不改，没有借此放宽调校或硬件保护。

原始探针仅初始化及读取。另做过一次原值写入尝试和一次1e-6矩阵偏移/恢复核验，已精确恢复原矩阵；没有切换预设、写Gamma或风扇参数。证据evidence/tuning-color-navigation/。真实六预设、HDR/远程会话和Gamma应用仍待回测。

## 0.9.3发布边界（2026-11-01）

准入走原Core平台探测、声明式驱动和真实通道。CoreCompatibility/CoreMachine管理结果；native_admission.rs提供查询20、机型风扇表查询21和有限EC桥接。项目号与本次探测值比较，不再按本机型号、BIOS或项目16通用放行。AMD/Intel温控分别编码。IP3/Mailbox不能套用经典EC协议，声明动作真实回读失败时不返回缓存成功。GPU固件保存遵循原Core能力和CPU编码，仍只修改下一次开机模式字节并完整回读。远端机型尚待实测，不宣称全部Core功能等价。

0.9.3沿用同一固定Core与桥接。本轮主要修改C#诊断、显示识别和界面，不更新上游或放宽协议保护。下方0.5—0.7及本机限制说明为历史阶段记录，当前要求以项目规范及RELEASE-0.9.3.md为准。


2026-10-30总览接入更新（开发版，未重新打包安装器）：query10返回按需CPU/GPU采样，query11只读WiFi/蓝牙/PTP/Fn状态，query12读取Core事件队列；独立`or_native_switch_set(id,expected,target)`只接受四个已知枚举、预期状态和目标。WiFi/蓝牙调用Core原生Radio函数，触摸板使用Core新增有超时的Windows切换函数与原EC掩码原语，Fn只改原bit4。C#硬件层仍核验机型/BIOS、共享寄存器完整回读及串行状态；未调用含首次配置恢复和批量PnP操作的总状态/总开关门面。

CPU在Core新增`system/native_metrics.rs`按需读取，原`CpuMonitor`的功耗估算函数提取复用；Energy Meter成功时标为实读，否则原版模型标为估算。GPU调用NVIDIA NVML，与原nvidia-smi使用同驱动口径和MHz/bytes/W单位，避免启动其永久子进程轮询器和假定初值。没有构造全局CPU/GPU监测线程；关闭DLL前显式释放PDH资源；原GPU温度读取与NVML新查询均平衡初始化/清理引用。

OSD使用原CoreEventSink接口及限长队列，Native原生窗口消费后显式ack，UI与硬件写入入口分开。没有新增全局键盘监听。源树在原NVAPI修正之外新增上述Core按需接口，固定上游提交仍为基线，不能再表述为“Core只有NVAPI改动”。66项后台、7项Rust及87项WinUI验证见`evidence/overview-connect/`。PawnIO仅完成源码/本机配置可行性核对，未接入、安装或调用，详见`PAWNIO-FEASIBILITY.md`。

0.6 更新：新增显卡模式、物理键盘类型与机型目录查询，以及仅由管理员辅助流程调用的显卡模式写入接口。读取权限不足时不采用上游的“默认混合模式”结果。当前集成状态与实机验证边界见 RELEASE-0.6.md；以下保留 0.5 时的只读互操作验证记录。

结论：可以复用原作者的 Rust 后端，WinUI 3 界面无需 WebView2。C# 需要一层 C ABI 调用桥，不能直接调用 Rust 的 `pub fn`。

已核对 main（核心 0.9.3）和 dev（核心 0.9.4）。本次固定 dev 提交 4ab36ff791a537de79cb8128a93f7dab247c3ee8：
https://github.com/faintonce/open-revo/tree/4ab36ff791a537de79cb8128a93f7dab247c3ee8/src-tauri/crates/openrevo-core

`Cargo.toml` 指定 `lib` / `cdylib`，实编译成功。但未修改的 DLL 导出表为零个函数，不能当成已完成的 C# SDK。Rust 的公开模块和函数只对 Rust 调用方开放，不是稳定的跨语言 ABI。证据：evidence/upstream-core-exports.txt。

## 已完成的调用桥

src/OpenRevo.CoreBridge 输出 openrevo_native_bridge.dll，导出 ABI 版本和只读查询接口。src/OpenRevo.Core/UpstreamCoreLibrary.cs 使用绝对路径加载 DLL；双方通过调用方管理的 UTF-8 缓冲区交换 JSON，不跨语言释放堆内存。无裸 EC 读写入口、插件执行入口或自动初始化工作线程。

已由 .NET 实际调用原版办公 / 均衡 / 狂暴风扇常量及电源状态，验证非法模式拒绝、连续 100 次读取。动态依赖与 Cargo 依赖树没有 Tauri / WebView2。此结果只证明所导出只读接口的互操作成功，不代表整套硬件功能已经接入。

电源状态读取来自上游函数。它在 Windows API 失败时默认返回 100% / AC，不能据此独立判断高功耗操作安全；现有控制边界仍采用自己的可空状态判断。

## 接管边界

上游 `EcBridge::new()` 会启动没有显式停止接口的循环线程，包含 AP 心跳写入、阈值修复及部分强冷维护；`auto_load()` 还可能进入驱动初始化流程。因此不能直接放进 WinUI 构造函数，更不能把任意原版命令全部暴露给插件。

后续在现有机型 / BIOS / EC 项目核验、进程冲突检查、串行控制和操作日志之内，逐项接入上游类型化接口；后台线程需具备可停止的生命周期。普通模式直接操作，自定义高危参数使用编辑草稿，点击应用才提交。

0.5.0 安装版仍使用已验证的本机控制层；调用桥为独立验证产物，尚未替换所有生产硬件路径。启动自动接管只读取并建立当前状态，不自动重放自定义功耗、超频或任意 EC 值。

## 可重建源码

当前未打包开发版的 GPU 路径：`or_native_gpu_stage(expected,target)` 只在已验证 GM6IQ0C / Intel / UniWillVariable 分支修改显卡模式字节，保留其余字节并比较完整回读。使用 Core 的固件读写原语，避开 `write_gpu_mode` 中的预唤醒和运行时 ACPI 同步，不停止显卡服务、不重扫设备、不自动重启。旧 `or_native_gpu_set` 导出拒绝调用。新 C# 接口与新 DLL 必须配套。

按需提权助手使用仅当前用户可访问的命名管道回传结果，双向核对连接进程 PID；管道不接收执行命令，助手只接受限定模式参数，完成后退出。只读查询与写入分离；读取允许与原版并行，保存仍核验机型、BIOS、EC 项目和其他控制程序冲突。本次开机的显示缓存不构成写入授权，真正保存前会重新核验固件状态。

0.7.1 新增 query 8（相机供电、策略与用户访问状态）和独立 `or_native_camera_power_set`。正常开关复用 Core 的 `wmi_modify_ec_mask`，只开放相机供电位；C# 外层核验精确机型和 BIOS，内外层都回读并保留其他位。`or_native_camera_access_allow` 仅供一次性设备恢复助手使用，先检查管理员令牌，再调用上游相机访问设置函数；普通开关不调用它。未直接调用批量操作 Camera/Image 类且自动保存原版配置的 `set_switch("camera",...)`。

0.7 的生产调用桥另提供 query 7（默认麦克风状态）和独立 `or_native_microphone_set` 导出；写入完成后回读确认。针对固定上游版本音频函数未平衡 COM 初始化的问题，桥接层按每次调用平衡初始化和释放，不启动原版 watcher。显卡写入同样位于独立导出，按需提权后调用。相机、Win 键锁和电源请求使用本机受限适配，不调用原版的全局隐私注册表修改或输入屏蔽逻辑。

src/OpenRevo.Upstream 保存上述提交的 core、config、resources 和 LICENSE，另加独立 workspace 清单；2026-10-29 极客接入已局部修改 `driver/nvapi.rs` 的 P0/domain 解析、对齐缓冲区与频率范围核验，详见 [TUNING-INTEGRATION.md](TUNING-INTEGRATION.md)。src/OpenRevo.CoreBridge/Cargo.lock 固定调用桥依赖。原作者许可为 GPL-3.0-or-later；调用桥沿用该许可。

执行 src/OpenRevo.CoreBridge/build.ps1，可编译调用桥并运行 C# 只读验证。需 Rust MSVC 工具链、Visual C++ 构建工具与 .NET 10 SDK。初次构建需下载锁定的依赖。
