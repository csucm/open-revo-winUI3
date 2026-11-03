# 共享.NET安装引导

REQ-127，2026-11-02。当前发布默认不内置.NET 10，使用framework-dependent部署。历史内置版和GitHub已发布0.9.7保持不变。本次独立审阅包名带Shared。

RuntimePrerequisite.iss在安装器初始化时探测x64全局.NET根目录。先读取HKLM64的InstalledVersions/x64/InstallLocation；未注册时使用系统64位Program Files/dotnet。检查dotnet.exe为64位PE、稳定10.0补丁目录，以及Core、Desktop、hostfxr关键文件。只有x86、9/11、预览版、缺项或失效目录不能通过。此为部署完整性门槛，不替代运行时损坏诊断。

已满足时跳过依赖页。缺少时首先显示依赖页，不能进入安装文件阶段。主按钮只从用户指定的https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe下载至安装器临时目录，随后打开微软交互安装程序；不打开版本选择页面，不要求寻找下载文件，不绕过微软安装确认/UAC，不自动重启。下载取消、失败、微软安装取消或复检失败均保留在引导页。完成后复检并进入正常安装。第二个可选说明链接为https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0。

静默安装缺依赖时直接失败，不静默下载或安装.NET。PrepareToInstall还会再次检测，避免向缺运行时的系统写入应用。Windows App Runtime部署保持原状，此引导没有宣称已检测所有独立系统依赖。

Native默认SelfContained=false、RollForward=LatestPatch；runtimeconfig要求Microsoft.NETCore.App 10.0.0并使用本机10.0补丁版。按用户要求检测Desktop包，因为该包同时提供Core运行时。build.ps1改用独立app-<版本>-shared及source-<版本>-shared输出，拒绝coreclr/hostfxr/hostpolicy/CoreLib残留。Compact参数只用于安装器压缩，不再导入自包含裁剪实验。

参考：https://learn.microsoft.com/en-us/dotnet/core/install/how-to-detect-installed-versions；https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0；https://jrsoftware.org/ishelp/topic_isxfunc_downloadtemporaryfile.htm。

验证与证据将保存于evidence/package-0.9.7-shared。不得以夹具/编译通过代替干净电脑下载、微软安装与真实升级验收。不自动安装.NET或Open-Revo，不覆盖远端Release。
