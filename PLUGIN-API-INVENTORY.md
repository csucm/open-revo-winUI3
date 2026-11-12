# 原生插件接口清单

2026-11-07，REQ-NATIVE-PLUGIN-API-01。核对当前生产源码，不把SDK类型或在制PluginPlatform类当作已接通能力。用户所说“主题窗口”已确认是r1缺失的窗口配色，不是独立插件窗口。

## 本次补齐的接口

API主版本仍1，次版本为2。软件公开号0.6.9、内部追踪0.9.87不变。

| 接口 | 作用与边界 |
| --- | --- |
| `IPluginContext.QueryCatalogAsync()` | 经`capabilities.catalog`获取完整能力清单。包括状态、单位、参数域、接管要求和未接通原因；只读，不授予权限、不增加采样 |
| `IPluginContext.QueryThemeSchemaAsync()` | 经`ui.theme.schema`获取当前窗口配色契约：static、main/mini/menu/dialog、light/dark、8个颜色令牌、RRGGBB，当前最多64个槽位。`SupportsDynamicUpdates=false`，不冒称worker可随意改色 |
| `UnsubscribeSummaryAsync()` / `UnsubscribePowerAsync()` | 补齐Host原本已有但SDK缺失的取消订阅入口。只操作当前插件自己的订阅 |
| `PluginSystemSummary.Charging` | 转发已有`SystemSnapshot.Charging`，未知保持null。沿用原样本时间，不增加电池读取或闲置轮询 |
| `minimumApiMinor` | 可选清单字段。使用新方法的插件填2，不满足的宿主拒绝；旧插件缺省0 |
| `description` / `author` | 可选原生卡片元数据。纯文本，分别最多512/80字符，不含HTML/XAML |

新SDK入口位于PluginSdk/PluginDevelopment.cs，WorkerContext转发，PluginSession分派，NativePluginServices提供真实元数据。NativePluginCatalog只在可用性发生变化时更新，不在每轮采样重建清单。可查询不代表可调用硬件；实际动作继续核对授权、能力、接管、参数、忙碌和回读。

## 已提供

| 能力 | 实际路径及限制 |
| --- | --- |
| 原生页面 | `PluginViewPolicy`与`NativePluginSurface`：text/button/toggle/choice，最多32项、有序行。主程序不加载插件XAML或程序集 |
| 静态窗口配色 | `PluginThemePalette`、`NativePluginThemePalette`及`NativePluginThemes`：四表面颜色、对比度/冲突校验、停止撤销。r2已获用户“可以生效”反馈，本次保留该通路 |
| CPU、内存、电量、供电、充电状态 | `Telemetry.SystemSnapshot`→NativePluginIntegration→PluginSystemSummary；复用既有样本 |
| 电源通知 | `NativePluginPowerQuery`与Windows电源通知；按需首次读取，不恢复藏盘遥测轮询 |
| 独立配置 | `PluginSession`的config.load/save；每插件ID隔离，JSON对象16KiB、原子保存，未知版本/损坏不覆盖 |
| 白名单导航 | ui.navigate只允许overview/display/power/devices/settings，不等于插件页面注册 |
| 后台逻辑 | 明确获准的lifecycle.background；AppContainer/Job内运行，受进程及队列限制 |
| 性能、强冷、限充 | `ApplyModeCommand`、`ApplyFanBoostCommand`、`ApplyBatteryCommand`；模式1/2/3、bool、60/80/100，仍按机型及供电保护。命令发送与回读确认分开 |
| 开关及禁用列表 | 已发现包独立于活跃会话。关闭保留卡片/最后页面数据并禁用交互，原子保存关闭选择；刷新和重新发现保持关闭 |

当前默认源码和build.ps1均启用原生插件入口，不再要求开发者额外打开测试开关；可显式传`-NativePluginFolderEntry:$false`回到旧入口用于排查。没有插件时不会创建worker或采样。

## 原生功能存在，插件尚未接通

| 功能组 | 查到的拥有者 | 缺失部分 |
| --- | --- | --- |
| 屏幕亮度、刷新率 | Core/DisplayControl.cs，Native/DisplayPage.cs、DisplayTopologyRefresh.cs | 选屏、可用范围、变更确认及超时恢复的插件契约 |
| RGB/白色背光与灯区 | Core/Hal/KeyboardLighting.cs、MonochromeLighting.cs，Native/PaintedLighting.cs | 按机型过滤的读值、颜色/亮度及事务回执 |
| Wi-Fi/蓝牙/触控板/Fn锁 | Core/OverviewCore.cs | 逐项读取与预期状态比较写入的插件代理 |
| GPU/风扇/温度等详细状态 | Core/OverviewCore.cs、WindowsGpuTelemetry.cs，Native已有状态 | 单位、时间、未知原因、按需订阅及脱敏的稳定快照 |
| 相机、麦克风、Windows键锁 | Core/CameraControl.cs，Native/QuickActions.cs、WinKeyAction.cs | 显式目标、设备身份、租约及异常/退出恢复 |
| 保持唤醒、熄屏 | Core/SessionPower.cs，Native/QuickActions.cs | 当前插件控制归属、输入唤醒与释放契约 |
| Windows电源方案 | Core/PowerPlans.cs、PowerPlanRecovery.cs | 预期状态、失败回退与用户确认 |
| GPU模式及GPU调校 | Core/Hal/GpuModeControl.cs、GpuTuning.cs | 机型能力、物理设备范围、忙碌/重启及确认边界 |
| 风扇曲线、CPU调校 | Core/Hal/FanCurveEditing.cs、DeclaredCpuTuning.cs、CustomTuning.cs | 完整预检、范围、提交及恢复事务 |
| 屏幕调色 | CoreBridge及Upstream/system/display_tuning.rs | 声明、应用、矩阵回读和撤销 |
| OSD主题颜色 | Native/NativeOsd.cs专用绘制 | 未纳入本次四表面Palette，不能声明已开放 |
| 滑条、嵌套布局、图表 | Native/PaintPrimitives.cs及既有原生视图 | PluginControl词汇和校验当前只接受4种控件 |
| 插件导航/独立窗口 | SDK PluginPage、在制平台类型；原生窗口基础存在 | Native没有登记/生命周期消费路径，不提供独立窗口接口 |
| 动效与按键映射 | MotionDesign、PhysicalModeKey、AcpiModeKeyObserver | 动画目标/撤销、按键归属、用户编辑及原生回退 |
| 临时策略、正常启用后启动恢复 | 在制PluginPlatform/PluginPlatformStore | Native未接入完整策略归属、暂停、恢复及启动流程。当前保存关闭选择不等于已完成正常启用的自动恢复 |

上述项目只列为`not_wired`，不绕过原有保护直接暴露内部方法。本轮不扩写已验收硬件协议或新增采样。

## 没有当前数据来源，或明确不开放

- 电池循环次数：当前C#摘要及所查Core状态链没有真实循环字段，标记`not_exposed`。不推断机型不支持，也不伪造数字。
- Windows系统菜单配色：由系统拥有，未有本软件合法颜色契约。
- 原始EC/Core编号、驱动句柄、Shell、外部进程、任意文件或DLL路径：不开放。
- 主程序接管、数据重置、更新安装、自启和基础配置：不向第三方直接开放。

## 原版卡片参考与验证边界

管理页结构参考本地可核对的原版dev固定提交`33c41aa9639a7814b7476b7a7485ec84497a9494`：PluginsView.tsx下方自定义插件区，及index.css的plugin-section/plugin-item样式。采用左侧图标、名称/ID/版本/描述、右侧胶囊开关，使用现有Paint组件；字号最低12，未复制原版10px小字。没有将该提交宣称为本轮重新联网核验的最新远端。

本次按用户“后面自己来”的要求只构建、静态核对和打包，不启动主程序、预览或自动测试，也不操作硬件。独立预览与生产共用NativePluginCards，供后续手动验收。InterfaceInspector示例可在新测试包手动载入，查看真实目录/格式查询及取消订阅效果；它不申请硬件权限。
