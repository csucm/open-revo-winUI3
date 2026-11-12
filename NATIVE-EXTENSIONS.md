# 原生扩展小接口（方案①）

本文只描述旧的可信内置接口。新的第三方沙箱框架见[NATIVE-PLUGIN-FRAMEWORK.md](NATIVE-PLUGIN-FRAMEWORK.md)，开发说明见[plugin-dev/README.md](plugin-dev/README.md)。两套API不能互换；本文关于没有IPC/第三方运行器的描述只适用于该旧接口。新框架源码已有，但真实隔离和界面尚未验收。

2026-11-02，REQ-124。实现于C#主线；未修改C++原型。方案①对应候选A；方案③对应候选C，等待具体旧网页插件需求。

## 已实现范围

`OpenRevo.Core/NativeExtensions.cs`没有WinUI依赖。宿主登记可信扩展身份并传入唯一导航回调；插件不能自行声明权限。本轮只注册内置`builtin-system-summary`。尚无第三方清单导入、通用控件解释器、脚本执行、IPC或网页宿主；不能据此声称已有完整第三方插件系统。

| 接口 | 契约 |
| --- | --- |
| Register(id, apiMajor) | 只接受主版本1；ID为1–64个ASCII字母/数字/连字符/下划线，重复拒绝。由可信宿主调用 |
| QueryCapabilities() | 返回API 1.0、主题、动作白名单、配置上限，以及硬件/网页能力false；返回数组独立复制 |
| Subscribe(topic, observer) | 目前仅system.summary；返回可释放订阅。存在最近样本时立即回送，保留真实时间与null，不补造未知值 |
| Execute(action) | 仅ui.open.overview/display/power/devices/settings。未知动作不进入导航回调。结果为ok/disabled/unsupported_action/host_action_failed |
| LoadConfig / SaveConfig | JSON对象，外层version=1与data；整个UTF-8文件最大16384字节，深度16。读取失败上报；原子保存复用AtomicPreferenceFile，不先删旧文件 |
| SetEnabled(false) / Dispose | 释放订阅，禁用后拒绝动作与新订阅。重新启用不复活旧回调；仍允许独立配置保存 |

接口限定宿主创建线程。未来IPC必须先验证客户端身份和能力，再调度至拥有线程；不可直接暴露Register或任意文件路径。此处不是第三方代码沙箱。新增硬件动作必须独立审查并复用现有机型、冲突、串行、提权和回读保护；本轮没有硬件动作映射。

状态字段：SampledAt（带偏移时间）、CpuPercent（%）、UsedMemoryGiB/TotalMemoryGiB（GiB）、BatteryPercent（%）、OnAc（bool?）。状态来自MainWindow.Tick已有Telemetry采样，无新增线程、计时器或Core查询。没有订阅时不触发额外采样。窗口隐藏时遵循现有暂停规则。回调失败只移除该订阅。

配置位于当前`AppData.DirectoryPath/extensions/<宿主ID>.json`，随现有数据迁移/重置机制处理。页面保存后才改变运行状态；失败保留旧配置并显示错误。损坏或未知版本配置禁用示例，保留文件供处理，不静默重置。

## 页面

PluginsPage.cs复用既有页头、卡片、语义主题与字体/减少动画策略。包含原生扩展示例、可用能力、网页兼容状态三组。页面Loaded订阅，Unloaded释放，关闭宿主释放全部订阅。启停持久化；跳转按钮走同一白名单接口。首批仅有内置示例，不提供无效的市场/安装按钮。

本轮核对Radix颜色用途，继续使用现有语义资源，不新增硬编码颜色：https://www.radix-ui.com/colors/docs/palette-composition/understanding-the-scale 。

## 验证与接续

后台检查入口：OpenRevo.Checks `--native-extension-checks <报告路径>`；隔离临时配置，不加载真实插件或写硬件。证据位于`evidence/native-extensions/`。增量预览构建输出为根目录`work/plugin-api-preview/`，未自动启动窗口、打包、安装或上传。不得将可编译等同真实WinUI视觉和交互验收。此隔离输出未校验完整桥接运行负载，不作为发行包。

继续时先做实际插件页启停、导航、重新进入、配置失败反馈与深浅/窄窗口验收。有明确网页插件样本后，逐项核对DOM/CSS/旧API依赖，再增加可选兼容组件。C++迁移可复用此契约，但需单独实现语言适配和验收；不要把C#接口当成已存在C++/IPC ABI。

