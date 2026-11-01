# 安装目录与大小面板切换修正（Fix2）

> 目录浏览的自制列表已在REQ-122/Fix3撤回。以ORIGINAL-INSTALLER-BROWSER.md为准；本报告的面板切换证据保留。

日期：2026-11-01。要求：REQ-121。安装包 19,586,444 字节（19.59 MB），版本0.9.6 Compact Preview Fix2。Fix1两项可靠性修复保留；Core、CoreLib和桥接DLL与Fix1逐字节相同。

## 安装目录

新安装默认当前用户 %LOCALAPPDATA%\Programs\OpenRevo。在本机通常为 C:\Users\Csl\AppData\Local\Programs\OpenRevo；用户示例省略的用户名不作为字面目录创建。
UsePreviousAppDir=yes保留同AppId已有安装路径，避免升级后旧位置留一份、另在新位置装一份。此改动不是现有D盘安装的自动迁移。用户可在安装目录页主动选择其他位置。
浏览按钮使用安装器内的文件夹列表，FindFirst/FindNext只保留普通目录，过滤Hidden/System和文件；不读取或修改Explorer显示隐藏文件的全局配置。支持盘符切换、上一级、双击进入、选择当前或选中的父目录和填写应用文件夹名；已有同名应用文件夹不重复嵌套。
独立测试宿主编译共享的InstallerDirectoryPicker.iss，在项目夹具中实际枚举普通、隐藏、系统、隐藏+系统目录及文件。只留下普通目录，测试通过。宿主InitializeSetup直接返回False，未安装产品；退出码1是预期取消。完整浏览弹窗的人工可用性和实际升级尚未验收。
build.ps1源码归档已补充该公共.iss，Fix2源码包也包含它，避免离开当前目录后无法重建安装器。

## 视频与切换修正

输入视频8.50秒、30fps、2080×1394。抽帧见证据video。可见旧内容被套入新窗口形态，以及展开后短暂空白。原ToggleMini先Resize、再Move，最后Render，且ApplyPageDesign在切换后重新执行250ms整页淡入。
改为准备新控件、字体及目标宽度下的布局，然后一次MoveAndResize提交目标几何。大小切换不执行整页淡入，普通页面导航动效保留。mini控件顺序、分区和400×640逻辑尺寸不改。
同一HWND的中间几何变化由局部DWM cloak保护；窗口仍参与合成，布局提交后恢复先前cloak及动画状态。若cloak不可用，仅对原本可见窗口用隐藏/恢复兜底。等待渲染和提交均有超时，异常与窗口关闭也释放保护，避免永久丢窗。切换期间抑制mini失焦自动藏托盘，最大化恢复也置于保护内，避免提前显示恢复态。
依据：[DWM cloak与窗口动画属性](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute)、[合成提交接口](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.compositor.requestcommitasync)。渲染/提交等待有恢复兜底；超时本身不能证明像素已呈现。

## 验证与边界

Native构建0警告0错误；完整精简发布仍有 182 条IL警告，没有屏蔽。9个受保护程序集与未链接输出逐字节相同；最终Core/CoreLib/桥与Fix1相同，保留Fix1的159回归、8专项、120轮压力与6助手模拟证据。本轮未重复宣称执行这些未改Core的全部检查。
使用相同精简运行时的独立WinUI宿主，跳过硬件初始化，外层始终cloak、不激活用户窗口。浅深两主题共48次切换、196项检查通过：同HWND、目标模式、一轮Render、无整页淡入、有限等待、外层cloak不被内层误解除，以及原DWM状态恢复。窗口几何为小面板600×960、大面板1770×1275像素（本机150%）。
宿主计时含冷控件创建与隐藏合成等待，不能作真实桌面帧率或“零闪烁”证明。总切换中位 540ms，遮罩阶段中位 306ms；实际可见桌面仍可能有空档，需用户安装Fix2后复测视频所示场景。没有把状态检查当成实屏验收。
未关闭用户窗口、执行Computer Use、修改自启、写硬件、安装或上传。旧正式包、旧精简包和Fix1保持哈希。C++迁移源码不改。GitNexus无本项目索引，调用链按当前源码核对。

## 交付与复现

安装包及对应源码、SHA256位于2026-10-31/xu/outputs。安装前从托盘正常退出旧版，X只会藏到托盘。当前运行版没有自动替换。
源码内installer-compact-preview.iss是相对路径配方，包含公共目录浏览代码；COMPACT-BUILD.txt给出精简构建入口。源文件逐项与归档核对。
独立工作区work/panel-switch-fix，证据evidence/panel-switch-and-install。主要实现MainWindow.cs、PanelShapeTransition.cs、PanelCompositionGuard.cs、Design.cs、DesktopWindowSizing.cs、CaptionModeCommand.cs及TrayLifetime.cs。PanelShapeReview.cs是独立只读宿主入口，生产入口保持普通模式。
