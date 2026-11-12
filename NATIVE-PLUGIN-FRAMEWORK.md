# Open-Revo 原生插件框架

插件管理最新修订：用户确认配色生效后，最新源码默认支持原生插件页及窗口配色。管理页改为原版自定义插件区的卡片结构和右侧胶囊开关。关闭只停用功能，卡片及最后页面继续保留，关闭选择原子保存并在刷新时保留。API 1.2新增能力目录、主题格式查询、取消订阅和既有充电状态；清单可选minimumApiMinor、description和author。详见[接口清单](PLUGIN-API-INVENTORY.md)。完整平台尚未全部接通。

奶蛙主题测试修订：Plugin-Test-r2开发包已接入静态ui.theme颜色清单，覆盖共用Paint组件的大面板、mini、原生下拉和弹窗。载入立即应用，跨页保留，停止撤销；格式、对比度和槽位冲突先校验。纯数据主题不执行程序集，不创建worker；含逻辑的插件继续使用AppContainer。OSD、系统菜单、动效、按键、策略等完整平台目标未因本项完成。示例为`plugin-dev/NaiwaTheme/plugin.json`，用法与边界见同目录README.md。

当前入口修订：按用户再次简化要求，开发构建使用唯一`<安装目录>\plugins`。用户放入插件子文件夹后点「刷新载入」，页面直接显示功能；首次硬件/后台能力集中确认，授权按包摘要保存。停止保留文件与设置。安装器和开发构建创建目录，运行中不创建替代目录。界面仍由`NativePluginFolderEntry=true`开发开关控制，正式默认待确认。详见[evidence/plugin-folder-simplification-20261107/REPORT.md](evidence/plugin-folder-simplification-20261107/REPORT.md)。下文任意FolderPicker描述属于首版历史；06完整平台尚未接入。

后续设计状态：REQ-NATIVE-PLUGIN-06七轮关键访谈已汇总为[第一版设计评估稿](PLUGIN-PLATFORM-PROPOSAL.md)，来源见[问答记录](PLUGIN-PLATFORM-DESIGN.md)第7节。固定目录、刷新载入及上述静态颜色主题已接入开发入口。记住正常启用状态并重启恢复，以及新增导航、完整主题表面、动效、按键映射、临时策略等平台目标仍未全部接入，不把已有数据契约或设计确认当作运行验收。

2026-11-07。REQ-NATIVE-PLUGIN-01/02/03/04。开发说明见[plugin-dev/README.md](plugin-dev/README.md)，当前验证范围见[evidence/native-plugins-20261107/REPORT.md](evidence/native-plugins-20261107/REPORT.md)。

## 要求与确认

| 编号 | 用户要求与来源 | 验收条件 |
| --- | --- | --- |
| REQ-NATIVE-PLUGIN-01 | 本聊天首次要求：安全沙箱、与本体隔离、硬件只能通过已有接口 | 主进程不执行第三方程序集；先建立AppContainer、Job、身份核验和授权；失败不降级；越权、崩溃、卡死和撤权不能绕过代理 |
| REQ-NATIVE-PLUGIN-02 | 本聊天首次要求：原生框架、不要WebView2占用 | 用现有WinUI基础绘制组件呈现受限页面；不加载插件XAML/控件/DLL到主进程，不启动网页宿主 |
| REQ-NATIVE-PLUGIN-03 | 本聊天首次要求：便于后续开发的插件框架 | 独立SDK、版本化协议、清单、权限、模板、示例、中文开发说明和构建入口 |
| REQ-NATIVE-PLUGIN-04 | 用户在电池复测正常后明确恢复，并要求将电池纳入设计 | 独立限充权限，只允许60/80/100；复用已修复ApplyBatteryCommand，不改协议、准入或回读；区分命令发送与回读确认 |

用户逐项选择：宿主提供原生组件与布局API，逻辑在沙箱；明确启用的后台插件可离页/藏盘继续；首版无网络和任意文件权限，配置经宿主独立保存。暂停指令已由“可以继续了”取代。电池修复以用户验收及`jiaolong-battery-flag-fix-20261107/acceptance-integration.json`为保护基线，不扩大为所有机型实测。

## 模块与数据流

| 模块 | 职责 |
| --- | --- |
| `src/OpenRevo.PluginSdk/` | 无Core、WinUI、WebView2依赖的net10.0契约；类型化硬件调用、页面描述、有界通信 |
| `src/OpenRevo.PluginWorker/` | 仅在AppContainer内加载第三方程序集；连接可信宿主，路由响应与事件 |
| `src/OpenRevo.PluginHost/` | 清单与文件检查、摘要绑定授权、AppContainer/Job、IPC身份验证、配置与方法代理、启停和清理 |
| `NativePluginIntegration.cs` | 调度回UI线程，调用现有硬件队列和HAL；已有采样和Windows电源通知接入 |
| `NativePluginSurface.cs` | text/button/toggle/choice描述转为既有Paint组件；相同结构局部更新，不执行插件回调 |
| `NativePluginPreviewWindow.cs` | 独立只读设计预览；不创建MainWindow、插件worker或硬件会话 |
| `plugin-dev/` | 生成器、最小模板、状态订阅和显式硬件控制示例、构建说明 |

插件逻辑 → AppContainer独立进程 → 经核验的IPC → 宿主权限/参数/状态检查 → 现有命令 → 原回读/回执。

## 安全与生命周期契约

- 含逻辑的插件每次启用使用独立AppContainer身份，不授予OS网络或设备capability。暂存代码只读/执行；独立配置通过宿主代理。Windows仍有容器自己的临时存储和系统允许读取的基础资源，“无任意文件权限”不表示完全无磁盘访问。静态主题只解析有界JSON，不含执行代码，无需创建worker；不能藉此加载程序集、XAML或脚本。
- 挂起创建worker，核对AppContainer token、SID及零capabilities，加入Job后恢复。Job限制一个进程、256MiB进程提交和20%CPU；关闭Job终止worker，禁止创建子进程并限制桌面交互。最多四个启用/启动/停止中的插件。这些是首版内部限制，不是实际占用或性能承诺；没有磁盘配额保证。
- 宿主连接私有单实例本机管道的两端，核对连接来自宿主自身。Windows只把这一端点传给指定worker，worker进程在挂起状态经token核验，启动后发出就绪消息。插件无需按名称打开管道。JSON自报身份没有授权效力。
- 清单只申请权限。`PluginApproval`绑定精确包摘要；文件变化需要重新确认。暂存逐文件核对摘要，失败拒绝启动。运行时直到启动完成才激活lease；撤权不可重新激活旧lease。插件ID保留到启动/退出清理结束，避免重启竞争。
- 禁用先同步撤权，再等待启动和进程退出。离页只停止前台插件，后台插件必须有显式后台grant。退出及迁移/清空用户数据时停止插件。已经进入HAL的事务沿原保护流程收尾，不能在中途强行终止主程序硬件操作。
- 硬件只开放性能1/2/3、强冷布尔值、限充60/80/100。UI/执行边界再次核对授权、接管、能力和忙碌。复用ApplyModeCommand、ApplyFanBoostCommand、ApplyBatteryCommand；保留机型、供电、互斥、比较写入和回读。不开放接管、原始EC地址、任意Core命令编号、脚本、命令行、动态库路径或提权入口。
- classic回读成功返回`readback_confirmed`；声明式强冷/限充返回`command_sent`，不能冒称实体效果。未知和不支持不伪造数值。
- 第三方代码的故障边界以Windows沙箱为基础；不承诺任意恶意插件绝不会影响系统。管理员/内核被攻破及同权限宿主文件被外部篡改不在本框架自身可保证的边界内。真正的隔离/拒绝效果须实际验证。

## 轻量化与后台事件

没有启用插件时没有worker、额外计时器或采样。CPU/内存摘要复用已有Telemetry，`SampledAt`沿用原样本At，藏盘仍暂停。电源订阅先按需读取一次Windows电源状态；后台复用PBT_APMPOWERSTATUSCHANGE，合并通知后只读一次系统电源状态，不读EC，不恢复CPU轮询。相同电源状态不重复推送。系统事件不承诺固定周期，自动化插件必须处理未知、断开和过期状态。

## 通信与存储限制

API主版本1，次版本2，尚未公开发行。与旧`NativeExtensions.cs`可信内置接口是不同契约，不能互换。帧为4字节小端正长度加UTF-8 JSON，上限64KiB、深度16。拒绝无效信封、未知主版本、重复/未知字段和非预期方向；请求ID为递增正数，事件为0。中途取消、截断或无效帧关闭连接，不继续解析错位数据。

每插件每秒最多30个请求，硬件/导航动作间隔至少500ms，配置保存500ms，页面更新250ms。SDK正常页面更新按需等待260ms，不新增闲置轮询。宿主事件队列16项；超时、洪泛或慢插件导致会话关闭，不向UI采样线程施加反压。发送时限5秒，宿主动作等待10秒；取消等待不取消已进入HAL的事务。配置最多16KiB，按插件ID隔离，使用既有原子写入；保存失败保留旧文件，损坏/未知版本外层配置不静默覆盖。

包仅接受顶层普通DLL和plugin.json，最多64项、合计16MiB；不接受目录、重解析点、路径穿越或可执行入口。不是通用ZIP解压器。当前开发工作流使用安装目录下plugins中的子文件夹。

## 交付边界

用户要求简化为本地文件夹载入并自动显示。开发构建属性 `NativePluginFolderEntry=true` 在现有插件导航显示「打开插件文件夹」「刷新载入」和载入后的原生功能。硬件与后台能力集中在首次载入或包变化时确认。停止保留源文件与设置。当前页面插件会话不自动跨重启恢复。正式默认构建等待简化界面验收，独立只读预览用模拟反馈演示此流程。

用户授权后执行本机AppContainer隔离夹具并打开只读预览。运行结果和未验证范围见[运行记录](evidence/native-plugins-20261107/FOLDER-AND-ISOLATION.md)。公开号仍0.6.9。

## 官方依据

- [.NET插件教程](https://learn.microsoft.com/en-us/dotnet/core/tutorials/creating-app-with-plugin-support)：不可信代码不能安全加载到可信.NET进程，须使用OS/虚拟化边界。
- [AppContainer启动](https://learn.microsoft.com/en-us/windows/win32/secauthz/implementing-an-appcontainer)：SID、capabilities、ACL与进程属性。
- [Windows Job对象](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects)：进程集合、资源限制和终止管理。
- [命名管道](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createnamedpipea)：单实例、ACL及LOCAL命名限制。2026-11-07查阅。
