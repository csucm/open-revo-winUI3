# 原安装目录浏览恢复（REQ-122，Fix3）

已撤回Fix2的自制列表。现在保存并调用Inno原始DirBrowseButton.OnClick；原TSelectFolderForm、TFolderTreeView、系统图标、路径编辑、选择/取消和新建目录逻辑均由安装器原处理程序负责。只在浏览期间筛选Hidden/System目录。

筛选由安装器临时加载的86,528字节x86原生DLL提供。它只在当前浏览线程、当前安装器进程的目录枚举入口生效；退出原回调后恢复。不会修改Explorer设置、文件属性或磁盘目录。DLL是dontcopy资源，不进入应用安装目录，不增加应用后台进程或.NET依赖。SetupArchitecture=x86与此前安装器默认架构一致，应用仍是x64。

已验证：

- 原目录树图标列表存在，测试目录展开后仅有普通目录。
- 原始浏览回调确实打开TSelectFolderForm，原图标存在，自动取消后路径不变。
- 普通、隐藏、系统、隐藏兼系统4个目录，筛选前/中/后为4/1/4。
- 其他线程仍能枚举4项；嵌套作用域和50次重复启停正常。
- 100次仅匹配隐藏目录的扫描正常结束，进程句柄70→70。
- 264个应用运行文件与Fix2逐字节相同；405个src源码文件与Fix2相同。Fix1/Fix2和原0.9.6安装器哈希不变。
- DLL编译无警告/错误，完整Fix3安装器编译成功。

未执行真实安装、升级、硬件写入、改自启或关闭用户窗口。测试宿主的目录选择窗口在激活前遮罩并自动取消；不是完整人工视觉验收。新建目录等原逻辑未重写，但未逐项人工交互测试。手动输入路径仍按原规则处理；默认LocalAppData的隐藏祖先可作为所选路径节点存在，过滤针对浏览枚举。

新安装默认%LOCALAPPDATA%\Programs\OpenRevo；已有同AppId升级继续使用原目录。Fix2面板切换代码原样保留，其真实桌面残影验收仍待用户复测。本包为本地0.9.6精简预览Fix3，未上传。安装前请从托盘正常退出当前程序。

证据：evidence/original-installer-browser/。源码含共享.iss、原生DLL完整源码/构建脚本、DLL及专项检查；重建DLL需MSVC x86工具与Windows SDK。build.ps1会先重建DLL，再归档源码。

交付文件：

- Open-Revo-0.9.6-Compact-Preview-Fix3-x64.exe：19,683,249字节；SHA256 `F948EA883AABBBAD1DA51FB4BDA8769387CD13C3A36F313617215C86051EC481`。

- OpenRevo-0.9.6-Compact-Preview-Fix3-source.zip：1,351,903字节；SHA256 `BAAAA98CABF4860C6BBAB20C0D242CCBF6EE6D00BA9313544C3EB890585FCCCD`。
