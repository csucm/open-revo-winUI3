namespace OpenRevo.Native;
internal sealed record PaintRowSpec(string Id,string Label,string Description,string Kind,string Unit);
internal sealed record PaintCardSpec(string Title,string Icon,int Span,bool Collapsible,PaintRowSpec[] Rows);
internal static class PaintPageSchema
{
    internal static readonly System.Collections.Generic.Dictionary<string,PaintCardSpec[]> Pages=new()
    {
        ["display"]=[
            new("显卡模式","gpu",2,false,[
                new("display-0-0","当前生效","","gpu-status",""),
                new("display-0-1","选择显卡模式","","gpu",""),
            ]),
            new("内置显示器","display",1,false,[
                new("display-1-0","屏幕亮度","","range","%"),
                new("display-1-1","亮度预设","","brightness",""),
                new("display-1-2","屏幕刷新率","真实产品仅列出本机支持的频率。","refresh",""),
                new("display-1-3","拔电自动低刷","恢复接电后按既有策略处理。","toggle",""),
            ]),
            new("色彩与校准","color",1,false,[
                new("display-2-0","屏幕色彩增强","全局滤镜，影响所有显示器。","toggle",""),
                new("display-2-1","调色控制","色温、饱和度、对比度与 Gamma","link",""),
                new("display-2-2","ICC 配置","物理内屏；列表来自本机配置。","select",""),
                new("display-2-3","导入 ICC","保留文件验证和取消行为。","button",""),
            ]),
        ],
        ["power"]=[
            new("电池健康","battery",2,false,[
                new("power-0-0","充电策略与电量","","battery-health",""),
            ]),
            new("供电与性能","power",1,false,[
                new("power-1-0","关机 USB 供电","关机或休眠时为外部设备供电。","toggle",""),
                new("power-1-1","来电自动开机","AC Recovery · 接入电源后启动。","toggle",""),
                new("power-1-2","高性能电源模式","随性能档位联动 Windows 策略。","toggle",""),
                new("power-1-3","睡眠时进入办公模式","唤醒后恢复此前档位。","toggle",""),
            ]),
            new("睡眠与系统策略","awake",1,false,[
                new("power-2-0","智能睡眠守护","背包防烫模式 · 六项策略分别显示能力","link",""),
                new("power-2-1","Windows 基础电源方案","保留自建方案、刷新与修复入口","link",""),
                new("power-2-2","防休眠","保持系统与屏幕唤醒；退出程序后解除。","toggle",""),
                new("power-2-3","关闭屏幕","一次性息屏，电脑继续运行。","button",""),
            ]),
        ],
        ["sleep"]=[
            new("背包防烫模式","awake",2,false,[
                new("sleep-0-0","智能睡眠守护","总开关；单项能力分别核验。","toggle",""),
            ]),
            new("网络与应用","wifi",1,false,[
                new("sleep-1-0","待机断开网络连接","减少待机联网唤醒。","toggle",""),
                new("sleep-1-1","阻止离开模式","避免应用在待机时持续运行。","toggle",""),
                new("sleep-1-2","忽略阻止待机请求","管理应用的系统待机请求。","toggle",""),
            ]),
            new("休眠与唤醒","awake",1,false,[
                new("sleep-2-0","禁用 RTC 唤醒","限制定时任务自动唤醒。","toggle",""),
                new("sleep-2-1","ACPI 深度睡眠","仅对支持的硬件启用。","toggle",""),
                new("sleep-2-2","30 分钟转休眠","电池供电时，待机 30 分钟后休眠。","toggle",""),
            ]),
        ],
        ["plugins"]=[
            new("内置状态摘要","plugins",2,false,[
                new("plugins-0-0","启用状态摘要","关闭后释放状态订阅。","toggle",""),
                new("plugins-0-1","状态摘要","CPU 52°C · 电池 76% · 人工示例","status",""),
                new("plugins-0-2","打开状态总览","复用现有状态，不增加采样线程","link",""),
            ]),
            new("原生扩展能力","plugins",1,false,[
                new("plugins-1-0","状态订阅","面板与扩展共享状态。","status",""),
                new("plugins-1-1","页面跳转与配置保存","独立配置；失败保留旧文件。","status",""),
                new("plugins-1-2","硬件控制","暂未向扩展开放。","status",""),
            ]),
            new("网页插件兼容","display",1,false,[
                new("plugins-2-0","旧网页插件","HTML / CSS / JavaScript 插件当前不能直接运行。","status",""),
                new("plugins-2-1","插件市场","不展示安装入口或假列表。","status",""),
            ]),
        ],
        ["settings"]=[
            new("外观与面板","settings",1,false,[
                new("settings-0-0","应用主题","保存后保持选择。","select",""),
                new("settings-0-1","唤醒面板形态","","select",""),
                new("settings-0-2","mini 点击外部收起","完整面板不受影响。","toggle",""),
                new("settings-0-4","动效速度","支持更快、更慢或关闭。Windows 减少动画优先。","select",""),
                new("settings-0-3","OSD 屏幕提示","风格、大小、位置与预览","link",""),
            ]),
            new("运行与设备","power",1,false,[
                new("settings-1-0","开机自动启动","登录后最小化至托盘。","toggle",""),
                new("settings-1-1","硬件接管","开启时核验设备；关闭后停止控制。","toggle",""),
                new("settings-1-2","刷新间隔","隐藏面板后暂停界面采样。","select",""),
                new("settings-1-3","解除离电限制","电池 / Type-C 下允许高性能档位。","toggle",""),
            ]),
            new("Windows 基础电源方案","power",1,false,[
                new("settings-2-0","当前方案","列表以系统实际枚举为准。","select",""),
                new("settings-2-1","刷新列表","","button",""),
                new("settings-2-2","修复 / 重置计划","先备份，再重建内置计划。","button",""),
            ]),
            new("数据与诊断","settings",1,false,[
                new("settings-3-0","数据存储管理","打开目录、迁移与清除","link",""),
                new("settings-3-1","诊断与日志","导出诊断、额外调试日志","link",""),
                new("settings-3-2","后台与托盘","关闭窗口后继续运行。","button",""),
                new("settings-3-3","退出程序","退出后释放防休眠状态。","button",""),
            ]),
            new("关于","info",2,false,[
                new("settings-4-1","软件更新","手动检查社区版发布。","update",""),
                new("settings-4-0","开源许可","基于 faintonce/open-revo / openrevo-core","status",""),
            ]),
        ],
        ["osdsettings"]=[
            new("显示选项","osd",1,false,[
                new("osdsettings-0-0","OSD 屏幕悬浮提示","关闭后立即隐藏当前提示。","toggle",""),
                new("osdsettings-0-1","风格","","select",""),
                new("osdsettings-0-2","大小","","select",""),
                new("osdsettings-0-3","位置","","select",""),
            ]),
            new("效果预览","caps",1,false,[
                new("osdsettings-1-0","Caps 已锁定","图标、状态文字与背景一起进入。","osd",""),
                new("osdsettings-1-1","预览提示","进入 180ms，退出 150ms；减少动画时直接显示。","button",""),
            ]),
        ],
        ["storage"]=[
            new("当前存储","settings",2,false,[
                new("storage-0-0","数据目录","正式产品显示完整路径；预览不读取本机目录。","status",""),
                new("storage-0-1","打开目录","","button",""),
                new("storage-0-2","迁移存储","复制并校验，保留原目录；重启后使用新目录。","button",""),
            ]),
            new("重置本版数据","settings",2,false,[
                new("storage-1-0","清除数据","配置、方案和诊断移到相邻备份后退出。","button",""),
                new("storage-1-1","保留范围","不删除官方控制台数据，不取消 Windows 自启任务，不重置硬件档位。","status",""),
            ]),
        ],
        ["diagnostics"]=[
            new("诊断报告","settings",2,false,[
                new("diagnostics-0-0","导出范围","设备信息、运行状态、异常和审计；按现有导出实现。","status",""),
                new("diagnostics-0-1","打开默认目录","","button",""),
                new("diagnostics-0-2","选择位置并导出","取消选择不产生文件。","button",""),
            ]),
            new("额外调试日志","settings",1,false,[
                new("diagnostics-1-0","日志等级","","select",""),
                new("diagnostics-1-1","模块过滤","","select",""),
                new("diagnostics-1-2","查看日志","","button",""),
                new("diagnostics-1-3","关闭隐藏","仅隐藏额外调试区域，基础诊断继续保留。","button",""),
            ]),
            new("机型形态与能力预览","display",1,false,[
                new("diagnostics-2-0","开发者预览","","select",""),
                new("diagnostics-2-1","布局演示","只改变能力展示，不改变真实机型身份或硬件。","status",""),
            ]),
        ],
        ["color"]=[
            new("色彩预设","color",2,false,[
                new("color-0-0","预设方案","真实名称和列表来自 Core。","select",""),
                new("color-0-1","方案工具","","tools",""),
                new("color-0-2","全局滤镜","参数变化实时提交；不套用调校页的草稿规则。","toggle",""),
            ]),
            new("色温与鲜艳度","color",1,false,[
                new("color-1-0","色温","","range","K"),
                new("color-1-1","饱和度","","range",""),
            ]),
            new("明暗与层次","display",1,false,[
                new("color-2-0","对比度","","range",""),
                new("color-2-1","Gamma","","range",""),
            ]),
            new("RGB 独立增益","color",2,true,[
                new("color-3-0","红色增益","","range",""),
                new("color-3-1","绿色增益","","range",""),
                new("color-3-2","蓝色增益","","range",""),
            ]),
            new("生效状态","display",2,false,[
                new("color-4-0","滤镜回读","矩阵与 Gamma 分别核验；部分成功必须单独说明。","status",""),
            ]),
        ],
        ["tuning"]=[
            new("方案与草稿","tuning",2,false,[
                new("tuning-0-0","当前方案","内置方案名称与能力由本机提供。","select",""),
                new("tuning-0-1","方案工具","","tools",""),
                new("tuning-0-2","重新读取","读取后替换草稿；不会应用。","button",""),
            ]),
            new("CPU 参数","performance",1,false,[
                new("tuning-1-0","持续功耗 PL1","","range","W"),
                new("tuning-1-1","短时功耗 PL2","","range","W"),
                new("tuning-1-2","瞬时功耗 PL4","","range","W"),
                new("tuning-1-3","自定义温度墙","关闭时保留设置值。","toggle",""),
                new("tuning-1-4","温度墙","","range","°C"),
            ]),
            new("GPU 参数","gpu",1,false,[
                new("tuning-2-0","超级性能","使用硬件最高功耗额度。","toggle",""),
                new("tuning-2-1","基础功耗","","range","W"),
                new("tuning-2-2","动态增强","未读到能力时保留原参数。","toggle",""),
                new("tuning-2-3","动态增强上限","","range","W"),
                new("tuning-2-4","核心频率偏移","","range","MHz"),
                new("tuning-2-5","显存频率偏移","","range","MHz"),
            ]),
            new("风扇曲线与响应","fan",2,false,[
                new("tuning-3-0","自定义风扇曲线","关闭后使用工厂狂暴风扇表。","toggle",""),
                new("tuning-3-1","CPU / GPU 曲线","两个独立编辑器；坐标和单位固定显示。","curves",""),
                new("tuning-3-2","平滑调速","控制每一级响应时间。","toggle",""),
                new("tuning-3-3","每级响应时间","","range","ms"),
            ]),
        ],
        ["lightingzone"]=[
            new("灯区与效果","keyboard",1,false,[
                new("lightingzone-0-0","当前灯区","实际能力由设备决定。","select",""),
                new("lightingzone-0-1","灯区电源","单独控制此灯区，不改变键盘背光。","toggle",""),
                new("lightingzone-0-2","灯光效果","Logo 与 EC 控制器按真实能力过滤列表。","select",""),
            ]),
            new("亮度与速度","keyboard",1,false,[
                new("lightingzone-1-0","灯区亮度","","range","档"),
                new("lightingzone-1-1","灯效速度","固定速度控制器保留轨道并禁用。","range","档"),
                new("lightingzone-1-2","自定义颜色","HEX / RGB · 不包含透明通道。","color",""),
            ]),
            new("灯区状态","info",2,false,[
                new("lightingzone-2-0","不支持","本机未检测到此灯区；保留标签及说明。","status",""),
                new("lightingzone-2-1","能力未知","读取前不伪装为关闭；保留控件位置。","status",""),
            ]),
        ],
    };
}

