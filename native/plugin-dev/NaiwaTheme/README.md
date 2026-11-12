# 奶娃主题 · 奶黄

测试用原生配色插件。用户已确认参考奶蛙／奶龙二创形象，不是喂奶或养成游戏。取暖黄、米白及深褐配色，保留浅色和深色两套。

插件只有plugin.json，约9.5KB，无程序集、脚本、图片、网页或后台逻辑。清单使用既有平台schemaVersion=1、runtime=static和ui.theme权限。四个表面main/mini/menu/dialog各提供两套八个颜色令牌，共64项。宿主解析、校验并绘制，不执行第三方代码，也不创建worker。

需要Plugin-Test-r2或更新的主题支持；Plugin-Test-r1尚未接通主题接口，不能用它验证此插件。软件公开号仍0.6.9，内部追踪仍0.9.87。

使用方式：把NaiwaTheme整个文件夹放到安装目录plugins中，点击插件页“刷新载入”。切换页面和mini后颜色继续保留。点击“停止”恢复原色。停止不删除源文件；再点刷新可重新载入。当前不自动跨重启恢复。

可修改的令牌为background、surface、border、text、muted、accent、tint、subtle，颜色只能是六位RGB。宿主拒绝格式无效、不可读或占用相同颜色槽位的主题。未声明的颜色回退软件默认值。

本测试版覆盖共用Paint组件的大面板、mini、下拉菜单及弹窗。保留原功能图标、数据语义色、布局和动效。OSD及Windows系统菜单尚未接入本主题。

建议手动检查：载入立即变色、跨页保留、浅深切换、mini切换、下拉和弹窗、停止恢复、重复刷新、删除主题后刷新。同一颜色槽位的第二份主题应被拒绝，原主题继续生效。不要把本说明文件放进NaiwaTheme载入目录；包内仅保留plugin.json。

参考来源：[奶蛙外观](https://xn--yus445ar7kxgjn9htj5a.com/assets/studio/serious.webp?v=16)、[奶娃／奶蛙游戏合集](https://game.0z.hk/index.html)。本插件只使用参考配色，不附带这些网站的角色图片。
