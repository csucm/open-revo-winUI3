using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private static string SectionIcon(string title)=>title switch
    {
        "显卡模式"=>"gpu","内置显示器"=>"display","色彩与校准"=>"lighting","电池健康"=>"battery",
        "外观与面板" or "数据与诊断" or "数据目录与维护" or "当前存储"=>"settings",
        "运行与设备" or "Windows 基础电源方案" or "供电与性能" or "固件供电选项"=>"power",
        "显示选项" or "屏幕提示选项"=>"osd","效果预览"=>"caps",
        "睡眠与唤醒" or "智能睡眠守护" or "守护总模式" or "休眠与唤醒"=>"awake",
        "网络与应用"=>"wifi","原生扩展" or "内置状态摘要" or "扩展能力" or "原生扩展能力"=>"plugins",
        "网页插件兼容"=>"display","色彩预设" or "色调与饱和度" or "RGB 独立增益"=>"lighting","亮度与对比"=>"display","生效状态"=>"info","关于 Open-Revo"=>"home","诊断报告" or "导出诊断与日志"=>"info",_=>"settings"
    };
    private Grid? inlineOsdPreview;
    private TextBlock? inlineOsdCaption;
    private Button? inlineOsdReplay;
    private FrameworkElement? inlineOsdSample;
    private FrameworkElement OsdPreviewCard()
    {
        inlineOsdPreview=new Grid{MinHeight=200};
        var scene=(Border)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Background='{ThemeResource SuitePreviewCanvas}' BorderBrush='{ThemeResource DesignStroke}' BorderThickness='1' CornerRadius='12'/>");scene.Child=inlineOsdPreview;
        inlineOsdCaption=Ui.Text("",12,muted:true);inlineOsdCaption.MinHeight=38;inlineOsdCaption.Margin=new Thickness(0,10,0,0);
        inlineOsdReplay=Ui.Button("预览",(_,_)=>ReplayInlineOsd());
        inlineOsdPreview.SizeChanged+=(_,e)=>{if(Math.Abs(e.NewSize.Width-e.PreviousSize.Width)>.5)UpdateInlineOsdPreview();};
        inlineOsdPreview.Unloaded+=(_,_)=>{if(inlineOsdSample!=null)MotionDesign.Reset(inlineOsdSample);};
        UpdateInlineOsdPreview();
        return DetailSection("效果预览",SuiteVisuals.Stack(0,scene,inlineOsdCaption,SuiteSettingRow("预览提示","进入 180ms，退出 150ms；减少动画时直接显示。",inlineOsdReplay)));
    }
    private void UpdateInlineOsdPreview()
    {
        if(inlineOsdPreview==null)return;inlineOsdPreview.Children.Clear();
        var hint=Ui.Text("屏幕示意",12);hint.Foreground=new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);hint.Opacity=.72;hint.HorizontalAlignment=HorizontalAlignment.Left;hint.VerticalAlignment=VerticalAlignment.Bottom;hint.Margin=new Thickness(12,0,0,10);inlineOsdPreview.Children.Add(hint);
        string style=systemPreferences.OsdTheme=="pill"?"磨砂胶囊":systemPreferences.OsdTheme=="fade"?"向右羽化":"电影字幕";
        string position=systemPreferences.OsdPosition=="center"?"屏幕正中":systemPreferences.OsdPosition=="bottom-center"?"下方中央":"左上角";
        if(inlineOsdCaption!=null)inlineOsdCaption.Text=osdEnabled?$"{style} · {systemPreferences.OsdScale}% · {position}":"OSD 已关闭；重新开启后显示效果。";
        if(inlineOsdReplay!=null)inlineOsdReplay.IsEnabled=osdEnabled;
        if(!osdEnabled){var empty=Ui.Text("OSD 已关闭",14);empty.Foreground=new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);empty.HorizontalAlignment=HorizontalAlignment.Center;empty.VerticalAlignment=VerticalAlignment.Center;inlineOsdPreview.Children.Add(empty);inlineOsdSample=null;return;}
        var label=Ui.Text("Caps 已锁定",14);label.FontWeight=Microsoft.UI.Text.FontWeights.Medium;label.Foreground=new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
        var content=SettingsControls(ApprovedIcons.Create("caps",28,true,false),label);content.Spacing=10;
        var frame=new Border{Child=content,Padding=new Thickness(0,6,0,6),Margin=new Thickness(16),HorizontalAlignment=systemPreferences.OsdPosition=="top-left"?HorizontalAlignment.Left:HorizontalAlignment.Center,VerticalAlignment=systemPreferences.OsdPosition=="top-left"?VerticalAlignment.Top:systemPreferences.OsdPosition=="center"?VerticalAlignment.Center:VerticalAlignment.Bottom};
        if(systemPreferences.OsdTheme=="pill"){frame.Padding=new Thickness(10,8,18,8);frame.CornerRadius=new CornerRadius(24);frame.BorderThickness=new Thickness(1);frame.BorderBrush=new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(77,255,255,255));frame.Background=new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(156,36,49,69));}
        else if(systemPreferences.OsdTheme=="fade"){frame.Padding=new Thickness(12,8,32,8);frame.CornerRadius=new CornerRadius(4);frame.BorderThickness=new Thickness(3,0,0,0);frame.BorderBrush=(Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SemanticPink"];var fade=new Microsoft.UI.Xaml.Media.LinearGradientBrush{StartPoint=new Windows.Foundation.Point(0,0),EndPoint=new Windows.Foundation.Point(1,0)};fade.GradientStops.Add(new(){Color=Windows.UI.Color.FromArgb(166,15,23,42),Offset=0});fade.GradientStops.Add(new(){Color=Windows.UI.Color.FromArgb(64,15,23,42),Offset=.75});fade.GradientStops.Add(new(){Color=Windows.UI.Color.FromArgb(0,15,23,42),Offset=1});frame.Background=fade;}
        frame.Measure(new Windows.Foundation.Size(double.PositiveInfinity,double.PositiveInfinity));double scale=systemPreferences.OsdScale/100d;if(inlineOsdPreview.ActualWidth>32)scale=Math.Min(scale,Math.Max(.5,(inlineOsdPreview.ActualWidth-32)/Math.Max(1,frame.DesiredSize.Width)));
        frame.RenderTransformOrigin=new Windows.Foundation.Point(systemPreferences.OsdPosition=="top-left"?0:.5,systemPreferences.OsdPosition=="bottom-center"?1:systemPreferences.OsdPosition=="center"?.5:0);frame.RenderTransform=new Microsoft.UI.Xaml.Media.ScaleTransform{ScaleX=scale,ScaleY=scale};inlineOsdPreview.Children.Add(frame);inlineOsdSample=frame;
    }
    private void ReplayInlineOsd()
    {
        UpdateInlineOsdPreview();if(!osdEnabled||inlineOsdSample==null||!Design.MotionEnabled)return;
        var visual=Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(inlineOsdSample);var opacity=visual.Compositor.CreateScalarKeyFrameAnimation();opacity.InsertKeyFrame(0,0);opacity.InsertKeyFrame(.12f,1);opacity.InsertKeyFrame(.90f,1);opacity.InsertKeyFrame(1,0);opacity.Duration=TimeSpan.FromMilliseconds(1500);visual.StartAnimation("Opacity",opacity);
        var offset=visual.Compositor.CreateVector3KeyFrameAnimation();offset.InsertKeyFrame(0,new(0,5,0));offset.InsertKeyFrame(.12f,System.Numerics.Vector3.Zero);offset.InsertKeyFrame(.9f,System.Numerics.Vector3.Zero);offset.InsertKeyFrame(1,new(0,-3,0));offset.Duration=opacity.Duration;visual.StartAnimation("Offset",offset);
    }
    private Border SuiteSettingRow(string title,string detail,FrameworkElement control)=>SuiteVisuals.Rule(DetailRow("",title,detail,control,360));
    private Border SubpageLink(string route,string title,string detail)
    {
        var button=Ui.Button("查看",(_,_)=>{currentPage=route;Render();});button.Content=SettingsControls(Ui.Text("查看",14),ApprovedIcons.Utility("chevron",13));button.Background=new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);button.BorderThickness=new Thickness(0);
        return SuiteSettingRow(title,detail,button);
    }
}
