# 插件精简与兼容候选方案

2026-11-01评估；2026-11-02更新：用户已选方案①（A）的小接口，方案③（C）按具体需求可选。首批实现见NATIVE-EXTENSIONS.md（REQ-124）。下表保留候选比较。

用户要求几个兼顾精简与兼容的可行方案。本轮按兼容原版插件分析。原说明页已由PluginsPage.cs的内置原生扩展示例取代。最新路线为 C++/WinRT + WinUI 3，核心不依赖 WebView2，市场暂不做。

| 方案 | 方式 | 兼容边界与代价 |
| --- | --- | --- |
| A 声明式原生扩展 | 清单描述控件、主题变量、事件和受限动作 | 适合简单面板和规则；旧任意脚本与皮肤需迁移 |
| B 轻量脚本宿主 | 独立宿主嵌入 QuickJS，适配受支持的旧接口 | 适合无 DOM 逻辑；浏览器和 Tauri 接口需补齐，需超时与配额 |
| C 按需网页兼容包 | 可选 C++/Rust 宿主使用 WebView2，经 IPC 请求核心 | 适合独立网页插件；运行时增加网页进程，不能使整套 CSS 皮肤直接作用于 WinUI |
| D 旧界面兼容包 | 保留必要的原版 DOM、样式和接口 | 更适合依赖原版结构的皮肤；两套界面维护成本高，仍须逐项验收 |

当前选择：先建立A的小接口，有具体旧网页插件需求再增加可选C。B/D未获实施要求。不要同时开发四套机制。

统一版本协商、能力查询、状态订阅、受限动作和独立配置。旧接口适配层与内部 Core 分离。未知能力明确拒绝。硬件继续经过机型检查、串行、回读和失败停止，不能另起第二套硬件控制核心。

第三方代码不直接加载进管理员主程序。独立进程不等于安全沙箱，仍需普通权限启动、IPC 身份绑定和能力校验。集中采样按订阅启停。禁用插件清理定时器与回调。后台插件需要显式生命周期，隐藏页面不等于零占用。

下一步选后台规则、独立网页组件、整套皮肤各一份，核对接口和 DOM 依赖，分别标记原样兼容、适配后兼容、功能等价、不支持。实际插件清单和资源预算待定。测量基础版、启用插件、关闭插件后的包体、共享依赖、全部相关进程内存和 CPU。本轮未运行插件或测量性能，不承诺具体 MB 或兼容百分比。

来源：本地 PROJECT_SPEC.md、HANDOFF.md、CPP-WINRT-HANDOFF.md 和 MainWindow.cs；QuickJS 官方 https://bellard.org/quickjs/ ；WebView2 进程模型 https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/process-model ；分发 https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution 。上游 https://github.com/faintonce/open-revo 的网页说明提到声明式规则，但不是本地固定 Core 的实现证据。


