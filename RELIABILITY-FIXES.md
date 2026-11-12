# 精简预览两项可靠性修复

日期：2026-11-01。要求：REQ-120。已修复并完成针对性后台验证。实际安装升级和日常使用仍待验收。

交付 Open-Revo-0.9.6-Compact-Preview-Fix1-x64.exe，19,579,601 字节（19.58 MB），相比上一预览只增 14,687 字节。仍内置.NET10，Windows App Runtime方式不变。正式版本号保留0.9.6，Fix1用于区分本地预览。
对应源码 1,293,398 字节。安装器和独立源码的SHA256见同名.sha256。保留旧正式包、旧精简预览及其对应源码；不自动安装、关闭窗口、上传或写硬件。

## 配置保存

新增 Core/AtomicPreferenceFile.cs，AppData.Save与SystemPreferences.Save共用。独立临时文件写完后替换目标；不先删除旧配置。只对Windows错误5/32/33有限重试，最多6次，重试间隔20/40/80/160/320ms，合计等待620ms；I/O本身与调度时间另计。永久权限或持续占用最终仍抛出原错误，旧配置保持完整。临时文件清理错误单独记日志，不遮盖保存错误。两类配置各自序列化同进程写入，不建立随路径无限增长的锁表。
错误码含义参考[微软系统错误码](https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--0-499-)。Access Denied不一定是短暂占用；有限重试没有更改权限，也没有把它当成必定可恢复。

8项专项通过：App/System各验证短暂读锁释放后保存成功、持续读锁有界失败并保留旧内容、只读目标明确失败并保留旧内容、四线程并发写后JSON完整且无残留临时文件。
同一测试代码换回旧Core，短暂读锁和并发写测试失败；修复Core全部通过。具体占用原配置的外部进程仍未定位，不因此宣称修复了所有磁盘或权限故障。

## 更新助手

ReleaseUpdater.BuildInstallScript改用System.Security.Cryptography.SHA256.ComputeHash；读取流和算法均由finally释放，不依赖Get-FileHash或额外模块自动导入。
父进程退出等待、起始时间防PID复用、长度/SHA256校验、路径引用、NOCLOSEAPPLICATIONS和失败日志保留。只在校验通过后运行安装器。
六种Windows PowerShell5.1模拟全部通过：标准/继承特殊PSModulePath下，正确内容通过，错误哈希拒绝，短包拒绝。Start-Process被无害记录函数替代；真实安装器运行0次，没有为测试终止真实进程。
UpdateChecks保留原13项并增加对ComputeHash、资源释放和不再使用Get-FileHash的断言。

## 最终负载验证

使用最终发布Core与缩减运行时执行原159项回归，全部通过。120轮压力耗时 37.6 秒：6000组App/System配置保存/读取（各6000次保存）、240000次JSON往返、120000次Rust只读查询、1200次COM创建/读取/释放、12000次日志写入，零失败。
短时独立压力不是日常UI或长期内存测试；本轮没有再次执行Computer Use。
264项运行文件，76,547,497 字节。9个保护程序集与未链接构建输出逐字节相同。JSON反射、内置COM保持启用；Rust桥、原版素材、XBF/PRI和许可保持。
完整源码归档与主源码逐项核对，新增AtomicPreferenceFile和PreferencePersistenceChecks，产品App入口不含PackageProbe。相对旧快照只改变3个Core文件和2个Checks文件，另新增2个文件；C++/Rust源码不动。
后台构建0警告0错误；精简发布仍有180条IL警告，与前次相同，没有屏蔽。保守保留策略不等于全面裁剪安全。

## 安装与后续

请先从托盘正常退出当前Open-Revo，再安装Fix1；X按钮只是隐藏到托盘。代理没有替换正在运行的安装文件。
安装后可通过Core与Native DLL哈希确认新版；当前运行旧预览仍会保留原行为。真实升级、退出恢复、完整自动安装和长期资源趋势没有在本轮执行。
证据evidence/reliability-fixes；独立构建和脚本work/package-fixes。GitNexus无项目索引，调用链按源码和异常栈核查。生产默认裁剪策略没有变化，正式发布仍需单独指定版本及发布要求。
