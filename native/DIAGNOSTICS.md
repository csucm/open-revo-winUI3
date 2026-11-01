# 基础诊断与回测

适用：0.9.4及REQ-76—80的源码预览。旧安装包0.9.2不含这些修改。

## 入口与路径

发布负载：`app-0.9.4-final/OpenRevo.Native.exe`；安装器位于上一层`Open-Revo-0.9.4-Setup-x64.exe`。运行未安装的负载时须保留整个目录。用户自行从托盘退出旧版后打开。Computer Use保持停止，本轮没有自动启动。

设置页的导出诊断可选择文件位置。成功后显示实际路径。新版报告为`diagnosticsSchema: 3`。测试者应使用新版复现后再导出，旧版报告不会自动补齐历史细节。

默认基础日志位于`%LOCALAPPDATA%\OpenRevoNative\diagnostics\runtime`。若已迁移数据或使用便携数据，则在实际数据目录的`diagnostics\runtime`。默认目录不可写时，日志尝试`%TEMP%\OpenRevoNative\diagnostics\runtime`；均不可写则保留当前进程内存。导出仍需要可写的默认诊断目录和用户选择的目标位置。

## 记录内容与边界

- 各进程使用独立`session-*.jsonl`。记录版本、模块MVID、会话、进程、关联操作、步骤、耗时和结果。
- 异常包含类型、消息、堆栈、内部异常和HRESULT。只有真实Win32Exception才填写Win32错误码。Core状态码独立记录，不冒充Win32错误。
- 启动回读记录字段差异及身份格式，不保存用户账户值。显示记录活动路径、候选、接口、面板、过滤结果和API码。灯光记录能力、协议前置条件、读取阶段、请求及回读。OSD记录样式、DWM结果和布局。
- 基础诊断不受详细调试等级关闭。重复轮询错误按30秒节流，记录已压制次数；其余必要操作保留。日志失败不替换原始异常。
- 单文件约1MiB，保留当前及3份轮转。按最近活动保留8组进程会话。内存保留最近256条。导出内存最多256KiB，磁盘尾部每文件最多128KiB、合计最多1MiB，截断有标记。因此导出是有界诊断，不保证包含整段运行历史。
- 导出包括当前内存、最近跨进程日志和已知旧启动失败文本。对用户名路径、SID及常见凭据字段脱敏；不记录原始启动参数、私密配置或其他按键。

## 本轮修正与验收

自启不再对Windows任务XML做过严的字面比较。账户可解析到当前SID；省略Enabled按默认true。归属、启用和配置匹配分别判断。路径、用户、参数和未知动作保护仍保留。参考[UserId规则](https://learn.microsoft.com/en-us/windows/win32/taskschd/logontrigger-userid)及[Enabled默认值](https://learn.microsoft.com/en-us/windows/win32/taskschd/taskschedulerschema-enabled-settingstype-element)。本轮只做模拟，没有注册或删除真实任务。

显示不再限定本机面板ID，也不伪造未激活屏的分辨率与刷新率。使用[QueryDisplayConfig](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-querydisplayconfig)活动路径与[内置接口类型](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ne-wingdi-displayconfig_video_output_technology)。唯一内置屏才允许控制；外接、虚拟、克隆和多候选继续拒绝。本机只读读到2560×1600、165Hz、60/165Hz及BOE0A1F；非提权亮度探针返回0x80070005，未通过亮度实机验收。

灯光失败不再展示默认3/4，不中断GPU初始化。未添加GM6AQ7C专用寄存器猜测，未放宽协议保护。OSD修正扩展边缘样式及框架刷新顺序；白框消失仍待视觉回测。

31项诊断检查与70项后台检查通过，编译0警告0错误。证据在`evidence/diagnostics-hardening/`。没有硬件写入或桌面操作。旧0.9.2安装包与源码ZIP保持原哈希。

## GM6AQ7C下一步

测试者身份由用户报告为极光X、i7-12800HX、GM6AQ7C。请在此源码预览中分别复现启动错误、刷新率读取、键盘灯读取/操作和OSD。随后选择位置导出schema 3报告。若启动阶段退出且无法导出，可提供上述runtime目录的最新会话文件及startup-failure.txt。下一轮以实际失败阶段为依据，不仅凭产品名称选择协议。

## 0.9.5退出与资源趋势

`exit.*`记录退出请求、忙碌标志、等待、服务关闭和失败恢复。`core.services.shutdown.*`区分等锁、进入桥接和底层返回，保留耗时。恢复失败时先显示窗口，再等待提示；不静默跳过色彩恢复。

`process.resources`在启动和每30秒记录一次。字段为字节：WorkingSetBytes是总工作集，PrivateBytes是私有提交，ManagedBytes是当前托管占用。GcHeapBytes/GcCommittedBytes/GcFragmentedBytes来自最近一次GC信息，不能当作同一时点的进程总内存。还包括运行时长、峰值、GC次数、句柄、线程、GDI/USER、页面、渲染次数和退出标志。隐藏后继续记录，但UI线程阻塞时该采样也会暂停。

资源日志沿用既有轮转和容量限制，不强制GC，不裁剪工作集。排查增长时比较同一进程、同一计数口径和相近操作阶段；单份转储不代表连续趋势。旧版记录不能自动补齐。

