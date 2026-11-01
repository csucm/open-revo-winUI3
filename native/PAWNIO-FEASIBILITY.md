# PawnIO CPU温度与功耗读取：仅可行性验证

日期：2026-10-30。对应REQ-44。用户明确要求“只验证可行性，先不做”；本轮没有安装、启动或调用PawnIO，没有新增PawnIO/LibreHardwareMonitor依赖或修改安装器。

结论：源码与本机配置层面可行，本机实际读取能力和准确性尚未实测。PawnIO是驱动访问通道；需要配合签名的IntelMSR模块与传感器解释代码，不能把安装驱动本身当作准确性证明。

## 已确认依据

- 官方PawnIO网站说明：官方发行版有数字签名，硬件能力由加载的模块提供。[官网](https://pawnio.eu/)
- 官方IntelMSR模块列出核心/封装热状态、温度目标、RAPL单位与封装能量状态寄存器。[模块源码](https://github.com/namazso/PawnIO.Modules/blob/main/IntelMSR.p)
- LibreHardwareMonitor的IntelCpu已经调用PawnIO IntelMsr。核心/封装温度根据有效位、温度目标和热状态计算；CPU Package功耗由RAPL能量计数差/采样间隔换算，不用CPU占用率估算。[IntelCpu源码](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LibreHardwareMonitorLib/Hardware/Cpu/IntelCpu.cs)、[IntelMsr封装](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LibreHardwareMonitorLib/PawnIo/IntelMsr.cs)
- IntelCpu源码包含Family 6 / Model 0xB7的Raptor Lake分支。本机注册表标识为`Intel64 Family 6 Model 183 Stepping 1`（183=0xB7），型号i7-14650HX；这支持“值得实测”的判断，不代替本机读数验证。
- 本机已存在PawnIO 2.2.0.0，服务查询为RUNNING；驱动文件签名Valid，签名发布者Microsoft Windows Hardware Compatibility Publisher。该驱动原本已安装/运行，本轮没有启动它。
- 本机HVCI注册表配置Enabled=1；这只是配置值，未用该值冒充本轮运行态兼容性实测。CIM运行态查询在当前通道受限。

本机只读证据：`evidence/overview-connect/pawnio-feasibility-local.json`、`pawnio-installation-readonly.json`、`pawnio-signature-readonly.json`；服务状态来自只读`sc.exe query PawnIO`。

## 与当前采样的区别

| 指标 | 当前版本 | 可研究的PawnIO方案 |
| --- | --- | --- |
| CPU温度 | Windows处理器温区，不冒充核心结温 | 核心DTS/CPU Package，明确具体测点 |
| CPU功耗 | Core Energy Meter可用则读取，否则原版负载/频率模型且标注估算 | RAPL CPU Package能量计数差得到区间平均功耗 |
| GPU与显存 | 已有NVIDIA NVML真实驱动读取 | 继续NVML，不为这些数据引入PawnIO |

RAPL仍是处理器报告的封装能量数据，不代表插座整机功率。温区、CPU Package和最热核心也不是同一个测点。

## 后续可选路线，尚未实施

1. 优先在Core加入可选、受限的CPU只读传感器提供者，经现有CABI返回带来源/有效性的数据；Native UI不接收任意MSR/寄存器地址。
2. 复用LibreHardwareMonitorLib的CPU采样也是候选，.NET宿主接入较直接；只开启CPU，不开启主板/EC/风扇扫描，以减少与现有控制路径的重叠。
3. 选择路线前核对固定版本、模块/驱动签名和随包许可声明；实际测试需另获用户实施指令。仅读取核心/封装温度、RAPL能量状态，不改功耗、风扇或其他硬件设置。
4. 测试时比较相同测点和采样窗口，观察空闲、正常负载、离电与恢复；失败时回退并标明来源。未验证前不承诺特定误差或跨机型支持。
