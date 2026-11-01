# OpenRevo 正式图标

2026-11-02，用户选定并要求今后安装包统一使用。对应 REQ-123。

- `OpenRevo-square-source.png`：已确认的黑蓝方底原稿，保留作无损裁切源。
- `OpenRevo.png`：正式圆角版。黑色开放圆环、蓝色独立插件模块、白色圆角底。圆角半径为边长18%，四角外侧透明，内部保持不透明。用户2026-11-02明确同意程序精确裁切；RGB逐像素保持，仅改变四角alpha。
- `App.ico`：包含16、20、24、32、40、48、64、96、128、256像素的32位图像，保留圆角透明度。16像素角点有正常抗锯齿覆盖，不铺回方形白底。
- `Export-Icon.ps1`：从方底原稿生成圆角PNG与ICO，同时更新Native与Cpp的App.ico。固定几何裁切，不重新生成图案。
- `Assert-Icon.ps1`：核对两份源码ICO及可选发布目录与正式ICO一致。主build.ps1在构建前和发布后调用，C++原型脚本在构建前调用。
- `Assert-EmbeddedIcon.ps1`：只读检查EXE内嵌图标的全部图像。主build.ps1在应用发布后和安装器编译后调用，不一致就报错，避免仅检查旁边的App.ico。

圆角PNG SHA256：`C23DB2F456F730CDDA987D6AF3D599414F836E26749D72C160058ED7585798E4`。
圆角ICO SHA256：`D2927ED5845348958D822EADFB485DB46AA5AEEAA0C404B0B59D4C9CAF72CB53`。

Native的ApplicationIcon、标题栏、AppWindow和托盘使用Native/App.ico；安装器SetupIconFile直接引用Branding/App.ico。开始菜单、桌面快捷方式和卸载列表使用应用EXE内嵌图标。Cpp通过App.rc嵌入同一ICO，并继续用App.ico设置窗口与托盘。

后续从精简版或旧源码快照打包时，必须同步这份资源、生成后的App.ico和相应构建引用。不得直接复用带旧图标的EXE或旧发布目录。不要覆盖已交付的历史包。此目录位于src下，主build.ps1的源码归档会包含原图、ICO与脚本。

概念稿由内置image_gen生成。选定稿和提示词位于项目根目录outputs/icon-concepts-2026-11-02/SELECTED.md；方底裁切源与该选定稿逐字节相同。圆角采用用户授权的精确裁切，未采用有毛边的生成稿。

圆角验收见evidence/rounded-icon/verification.json。用户本轮明确只更新资源与配置，不重打包。现有0.9.7安装器检查为旧方底黑蓝图标10帧一致，不能声称已包含圆角；其哈希保持不变。
