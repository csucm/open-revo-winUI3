# 极客调校与交互修复（2026-10-29）

要求：REQ-16 / REQ-17 / REQ-18 / REQ-19，见项目规范。用户明确选择完整接入 CPU 功耗/温控、GPU 功耗/超频、自定义风扇曲线；保持草稿编辑后点击应用。

## 模块与提交边界

- `src/OpenRevo.Native/TuningPage.cs`：原生编辑器、重新读取、本机 JSON 草稿保存/载入、应用；`FanCurveEditor.cs`：CPU/GPU 各八节点图形拖动及数字编辑。打开编辑器不激活极客模式，草稿不会开机自动重放。
- `src/OpenRevo.Core/Hal/CustomTuning.cs`：类型化草稿、实读基线、固定协议、供电/冲突/身份/范围核验、串行提交与最终回读。修改中的曲线不写硬件。应用期间保持强冷；失败立即停止后续调校写入，并尽力保留强冷，要求重新读取。
- 功耗值和显卡控制地址仍集中于 `Vault.cs`，不暴露任意地址 API。已有自定义模式先退出其激活位再暂存新值，避免写入过程中出现暂态 PL1/PL2 倒置；完整参数备妥后才重新激活。实际事务不是物理原子操作，失败可能部分生效。
- PL1 ≤ PL2，PL4 ≥ PL2 + 20W；编辑上限沿用原版默认算法：工厂狂暴 PL1/PL2 + 25W、PL4 + 50W，同时受字节及瞬时余量边界约束。本机草稿从当前工厂档位读取，不把狂暴值误称为当前均衡值。
- CPU 温控目前仅开放本次已适配的 i7-14650HX，Tjunction 100°C，编辑墙值80–95°C。未知 CPU 编码拒绝操作。[Intel 官方规格](https://www.intel.com/content/www/us/en/products/sku/235996/intel-core-i7-processor-14650hx-30m-cache-up-to-5-20-ghz/specifications.html)。
- `GpuTuning.cs` 直接短时查询 NVML 硬件功耗边界，避免启动上游 global GPU 后台。固定基础功耗卡不写 cTGP 偏移；未知能力保留 GPU 参数。超级性能使用硬件最大功耗额度，手动 DB/cTGP 与超级性能分开；频率偏移受驱动可编辑范围以及原版250/1000MHz上限双重约束。
- 桥 query 9 只查询 GPU 名称、P0 频率偏移及范围；独立 `or_native_clock_offsets_set` 检查预期偏移、目标范围并回读。未调用固件模式切换、设备重扫、驱动安装或维护线程。
- `PerformanceControl.cs` 识别模式4，允许从已知极客风扇协议返回工厂模式；退出时重置显卡频率偏移。基线变化拒绝提交，不能用显示缓存授权调校。

## 上游局部修改

固定上游提交仍为 `4ab36ff791a537de79cb8128a93f7dab247c3ee8`，但本轮修改了 `OpenRevo.Upstream/crates/openrevo-core/src/driver/nvapi.rs`，不能再称其所有内容未修改。

改动：按实际 P0 与 Graphics/Memory domain ID 解析频率及编辑范围，拒绝畸形/缺失/重复域；不再假设返回数组顺序；使用对齐的 u32 缓冲区；写入前核验驱动范围，并跳过未改变的域。结构依据 [NVIDIA 官方 nvapi.h](https://github.com/NVIDIA/nvapi/blob/main/nvapi.h) 的 `NV_GPU_PERF_PSTATES20_INFO_V1` / `NV_GPU_PSTATE20_CLOCK_ENTRY_V1`。GPL 对应源码保留在项目内。

## 交互修复与验证

- 背光颜色菜单删除底部重复项，保留八个预设，详情自定义颜色继续使用。
- `SelectedChoiceStyle` 独立模板使选中边框独立于悬停/按下状态，保留系统键盘焦点效果。
- 亮度确认后的局部同步同时更新滑块、标题、预设按钮；不依靠整页 Render。拖动期间不从后台覆盖滑块。
- C# 默认检查55项、Rust测试4项通过，涵盖状态过期、供电、审计/冲突、曲线编码、未知显卡、失败回读与强冷、退出工厂模式、固定基础功耗保护等。
- 显式开发参数 `--ui-checks` 在真实 WinUI 进程内运行12项控件回归检查并退出，将结果写入 EXE 旁的 `ui-review.json`。检查普通/悬停/按下的实际模板状态、两主题边框、亮度联动、颜色项和编辑器构建/草稿事件，不调用硬件 setter，不注入鼠标键盘，不保存用户主题偏好。
- 本机只读证据：RTX 4070 Laptop GPU，基础功耗115W、DB25W、偏移0/0；工厂均衡75/75/200W，狂暴130/130/200W。均为本轮快照，不是用户要求的默认值。
- 真实 CPU/GPU/风扇调校写入、高负载稳定性、退出工厂模式的实机往返仍未执行；物理鼠标交互与完整视觉效果待用户审阅。未打包、未覆盖安装版、未重启。

证据目录：`evidence/hover-review/`。正常预览仍使用交接文档中的标准 bin 路径；`bin-review/` 是本轮为避免覆盖运行中产物而使用的中间构建目录。
