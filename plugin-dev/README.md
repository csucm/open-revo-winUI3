# 原生插件开发

当前API为1.2。最新源码默认启用原生插件入口；关闭后保留卡片和最后页面，并保存关闭选择，刷新不重新打开。新增QueryCatalogAsync、QueryThemeSchemaAsync和取消订阅方法，摘要补齐已有Charging字段。可手动载入InterfaceInspector检查真实接口；新方法的插件填minimumApiMinor=2。已接通能力与所有缺口见[接口清单](../PLUGIN-API-INVENTORY.md)。早期“开发开关等待启用”的段落仅作历史，不覆盖本段。

新增静态配色测试示例：`NaiwaTheme/plugin.json`。Plugin-Test-r2起可直接载入并改变共用原生组件颜色，无需DLL或worker，只申请ui.theme。格式和手动验收见[NaiwaTheme/README.md](NaiwaTheme/README.md)。它覆盖大面板、mini、原生下拉和弹窗，不包含OSD及Windows系统菜单。其他扩展贡献仍未接入。

当前载入流程：把构建好的插件目录放到`<Open-Revo安装目录>\plugins\<插件目录名>`，点击插件页「刷新载入」，功能自动显示。每个目录包含`plugin.json`与入口DLL等允许的文件。不要向使用者提供注册页面、修改路径或运行脚本的步骤。普通页面插件直接载入；首次硬件/后台授权集中确认，文件变动后重新确认。开发界面仍由`NativePluginFolderEntry=true`控制，正式默认待验收。历史隔离检查已执行，范围见[evidence/native-plugins-20261107/FOLDER-AND-ISOLATION.md](../evidence/native-plugins-20261107/FOLDER-AND-ISOLATION.md)；本文后续“仅编译、未运行”是首轮记录，不能代表这些历史结果或本轮新入口已验收。当前入口支持页面插件和上述静态主题，其他扩展贡献尚未接入，框架开发中的API 1.1不代表完整平台已交付。

当前SDK是开发中的API 1.2。它使用C#/.NET 10，界面由Open-Revo现有WinUI绘制。插件不引用WinUI、Core或硬件桥接，也不包含HTML/JavaScript。框架源码遵循仓库现有LICENSE，本轮未另授MIT等许可。

本轮只编译，尚未实际验收AppContainer与硬件边界。第三方安装/权限页面仍待独立预览确认；不能把下面的开发输出当成已发布插件市场或可安装包。

## 构建框架

在本地项目根目录执行；需要Windows x64、.NET 10 SDK及主项目已有WinUI构建依赖。

```powershell
& outputs/OpenRevo-Full-Migration/plugin-dev/Build-Framework.ps1 -BuildPreview
```

默认输出在`work/native-plugin-framework/`：sdk为独立SDK DLL，worker为可信沙箱运行器，examples有两个独立插件目录，preview为只读WinUI设计预览。脚本只构建，不启动、不测试、不操作硬件。构建日志与build-policy.json记录在同目录。不指定BuildPreview时只编译框架与示例。

checks为单独编译的无硬件隔离检查入口，checks/probe为专用夹具；它们没有安装到应用，也没有在构建时执行。夹具会尝试访问新证据目录里的合成标记文件、连接本机回环监听、请求宿主VM_WRITE句柄、创建一个马上退出的子worker，并故意卡住CPU以核对停用；不访问互联网或真实硬件。需要明确授权后才运行：

```powershell
dotnet work/native-plugin-framework/checks/OpenRevo.PluginChecks.dll `
  --run-isolation-checks work/native-plugin-framework/worker `
  work/native-plugin-framework/checks/probe work/native-plugin-isolation-run
```

证据目录必须全新；工具不覆盖旧目录。所有配置/日志隔离到该目录，结果写入isolation-results.json。这个入口只覆盖列出的隔离场景，不替代完整IPC、内存洪泛、并发、UI或机型硬件验收。当前仅编译，未运行。

## 创建第一个插件

```powershell
& outputs/OpenRevo-Full-Migration/plugin-dev/New-Plugin.ps1 `
  -Destination work/MyPlugin -Id my-plugin -Name 我的插件 -ProjectName MyPlugin

$pluginSdk = (Resolve-Path work/native-plugin-framework/sdk/OpenRevo.PluginSdk.dll).Path
dotnet build work/MyPlugin/MyPlugin.csproj -c Release "-p:OpenRevoSdkPath=$pluginSdk"
```

生成器不覆盖已有目录，不安装或执行插件。目标框架为net10.0，入口是public无参类，实现`IPlugin`。模板只申请ui.view，不获得硬件、后台、网络或文件权限。

构建完成后，开发包目录只放MyPlugin.dll与plugin.json；如有自己的托管依赖，可放同级DLL。不要复制Core、桥接、WinUI、整个bin目录、EXE、deps/runtimeconfig、符号文件或SDK DLL。框架在worker中统一提供SDK。首版没有NuGet依赖解析器或资源子目录；依赖部署不得假设主程序已经加载相同版本。

## 入口与页面

```csharp
public sealed class Plugin : IPlugin
{
    public async Task RunAsync(IPluginContext context, CancellationToken stop)
    {
        await context.SetViewAsync(new PluginView("示例", [
            new("status", "text", "状态", "等待操作"),
            new("run", "button", "执行操作")]), stop);
        await foreach (var item in context.ReadEventsAsync(stop))
        {
            // ui.event的Data可反序列化为PluginUiEvent。
            // 根据ControlId处理；收到其他topic时按对应数据类型处理。
        }
    }
}
```

首版页面是有序行布局，最多32项。宿主拥有字号、主题、行间距和容器，不接受XAML、绑定表达式、URI、动态类名或插件回调。id为1–64个小写ASCII字母、数字、下划线或连字符，首字符须为字母/数字。显示文字可用中文。

| Kind | Value与额外字段 | 事件 |
| --- | --- | --- |
| text | 纯文本Value，最多1024字符 | 无 |
| button | Label作为行标签，宿主显示执行按钮 | Value=null |
| toggle | Value只能为true/false字符串 | 返回新值字符串 |
| choice | 1–12个PluginChoice；Value须匹配选项值 | 返回所选Value |

同一页面id不能重复。相同结构仅更新值和启用状态；改变顺序/标签/选项会重建相应页面结构。SDK按需限制页面更新频率，不要自行创建永久刷新计时器。先处理事件，再发送最新页面；不要在未消费事件时无限等待另一个UI事件。

## 状态与后台

调用SubscribeSummaryAsync需要state.summary.read，事件topic为state.system.summary，数据为PluginSystemSummary。CPU首次采样可为null；null表示未知，不能补0。SampledAt来自真实宿主样本，藏盘后摘要暂停。

调用SubscribePowerAsync需要state.power.read，topic为state.system.power，数据为PluginPowerState。首次订阅按需查询Windows状态；后台响应系统电源通知，未知字段保持null。它不是固定周期的遥测接口。后台自动化必须自行判断时间戳、供电及条件是否真实，不要依据陈旧CPU读数控制硬件。

background=true必须同时申请并获准lifecycle.background。它允许离页/藏盘继续处理事件，不表示系统休眠时仍执行。前台插件离页会停止；重新进入需通过宿主重新启用。RunAsync须尊重取消令牌；不要自行拉起进程、提权、打开驱动或绕过broker。终止插件不会回滚已完成的硬件事务。

## 硬件能力

先QueryCapabilitiesAsync，再决定按钮是否可用。查询不是长期授权，执行时宿主会再次核验。清单申请不等于用户授予，也不等于当前机型支持。

| 权限 | SDK调用 | 允许值 |
| --- | --- | --- |
| hardware.performance.set | SetPerformanceAsync | Office、Balanced、Performance，即1/2/3 |
| hardware.fanBoost.set | SetFanBoostAsync | bool |
| hardware.battery.limit.set | SetBatteryLimitAsync | Workstation=60、Balanced=80、Full=100 |

例如：

```csharp
var result = await context.SetBatteryLimitAsync(PluginBatteryLimit.Balanced, stop);
// readback_confirmed：原HAL已回读确认。
// command_sent：声明式驱动已发送命令，实体效果待确认。
// permission_denied/unavailable/busy/rate_limited：没有完成请求。
```

不要把Success=true统一写成“硬件效果已确认”。不支持任意阈值、EC地址、Core函数编号、自动接管、Shell或提权。充电档位不会主动放电。插件应显示失败原因并停止重复提交，不能用死循环重试。

`HardwareControls`示例包含性能、强冷和60/80/100限充。加载、绘制、改变选项只更新草稿，必须点击应用才发命令；它不自动写硬件。`SystemSummary`示例演示订阅、后台电源事件、独立配置和白名单导航，不请求硬件权限。

## 配置

config.own允许LoadConfigAsync/SaveConfigAsync。Data必须是JSON对象，宿主外层version=1，总文件最多16KiB、深度受通信限制。宿主按ID选路径，插件不能提供路径。不存在时返回not_found；损坏、未知版本和保存失败不能静默重置。示例只有保存成功才修改已提交状态。

软件通过自身数据迁移/清空流程停止插件后再处理目录。撤权和配置删除是不同操作。要撤权先停止runtime session，再撤销持久授权；删除失败必须报告。

## 清单

```json
{
  "apiMajor": 1,
  "id": "my-plugin",
  "name": "我的插件",
  "version": "1.0.0",
  "entryAssembly": "MyPlugin.dll",
  "entryType": "MyPlugin.Plugin",
  "permissions": ["ui.view"],
  "background": false
}
```

已知权限还有state.summary.read、state.power.read、config.own、ui.navigate、lifecycle.background及上表硬件权限。未知权限/版本拒绝，不自动扩大。没有internet或任意目录权限。ui.navigate只允许overview/display/power/devices/settings，并在藏盘时拒绝强制跳转。

## 宿主接入者

`PluginRuntime`/`PluginPackage`/`PluginApprovalStore`仅供可信主程序。不要把它们交给插件，不要把Host或Core引用加入插件工程。

1. PluginPackage.Read检查目录并计算摘要；展示来源、身份、版本、摘要和申请权限。
2. 用户明确选择后，调用PluginApproval.FromUserSelection；选中的权限来自用户界面，不能直接复制清单自动同意。
3. 如需持久化，用PluginApprovalStore.Save原子保存。保存本身不启用插件。
4. runtime.EnableAsync(package, approval)才创建worker。更新后摘要不一致必须重新展示权限；Load返回null不能回退为全权运行。
5. Native适配器必须在真实执行边界再次检查lease，使用既有operationQueue与HAL。不要在UI线程等待插件代码、写管道或执行原子文件保存。
6. 退出、禁用、前台离页和用户数据维护均释放运行器。启动中禁用也必须等待清理结束，不能立刻复用相同ID。

主程序已包含惰性适配器及worker构建复制；生产导航没有新安装/授权入口。独立设计预览只使用共享原生组件和模拟数据，不启用第三方代码。页面确认和真实沙箱验收完成后，才能开放真实入口。

## 调试与验收

不能双击worker.exe调试插件；worker在普通进程中会拒绝加载。不要为调试增加无沙箱回退。先编译，再通过显式授权的宿主会话运行。诊断按plugin.*事件记录，不把配置内容、令牌或私密路径写入日志。

真实隔离验收应覆盖网络/越界文件/跨进程修改拒绝、两个插件身份隔离、非法/截断/重复消息、卡死/内存洪泛、禁用与启动竞争、退出清理。硬件实测单独授权，先核对机型和接管，再按原功能验收；不借插件验收扩大原协议范围。当前未执行这些运行检查。
