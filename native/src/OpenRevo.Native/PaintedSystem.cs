using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenRevo.Core;
using OpenRevo.ResidentProtocol;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private FrameworkElement? paintOsdSample;
    private FrameworkElement PaintOsdPreview()
    {
        var host = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        (bool Enabled, string Theme, int Scale, string Position, bool Dark)? rendered = null;
        void Update()
        {
            var current = (osdEnabled, systemPreferences.OsdTheme, systemPreferences.OsdScale,
                systemPreferences.OsdPosition, Paint.Dark(root));
            if (rendered == current) return;
            host.Content = BuildPaintOsdPreview();
            rendered = current;
        }
        Update();
        paintBindings.Add(Update);
        return host;
    }

    private FrameworkElement BuildPaintOsdPreview()
    {
        var scene=new Grid{MinHeight=200};var gradient=new LinearGradientBrush{StartPoint=new(0,0),EndPoint=new(1,1)};gradient.GradientStops.Add(new(){Offset=0,Color=Paint.Brush(Paint.Dark(root)?"354A64":"8196AB").Color});gradient.GradientStops.Add(new(){Offset=1,Color=Paint.Brush(Paint.Dark(root)?"202D40":"5E718A").Color});scene.Background=gradient;
        if(osdEnabled)
        {
            var label=Paint.Text("Caps 已锁定");label.Foreground=Paint.Brush("FFFFFF");var frame=new Border{Child=Paint.Inline(10,Paint.Icon("caps",28,true,false),label),Padding=new(0,6,0,6),Margin=new(16),HorizontalAlignment=systemPreferences.OsdPosition=="top-left"?HorizontalAlignment.Left:HorizontalAlignment.Center,VerticalAlignment=systemPreferences.OsdPosition=="top-left"?VerticalAlignment.Top:systemPreferences.OsdPosition=="center"?VerticalAlignment.Center:VerticalAlignment.Bottom};
            if(systemPreferences.OsdTheme=="pill"){frame.Padding=new(10,8,18,8);frame.CornerRadius=new(24);frame.Background=Paint.Brush("9C243145");frame.BorderThickness=new(1);frame.BorderBrush=Paint.Brush("4DFFFFFF");}
            else if(systemPreferences.OsdTheme=="fade"){frame.Padding=new(12,8,32,8);frame.CornerRadius=new(4);frame.BorderThickness=new(3,0,0,0);frame.BorderBrush=Paint.Brush(Paint.Accent(root,"rose"));var fade=new LinearGradientBrush{StartPoint=new(0,0),EndPoint=new(1,0)};fade.GradientStops.Add(new(){Color=Paint.Brush("A60F172A").Color,Offset=0});fade.GradientStops.Add(new(){Color=Paint.Brush("400F172A").Color,Offset=.75});fade.GradientStops.Add(new(){Color=Paint.Brush("000F172A").Color,Offset=1});frame.Background=fade;}
            frame.RenderTransformOrigin=new(systemPreferences.OsdPosition=="top-left"?0:.5,systemPreferences.OsdPosition=="bottom-center"?1:systemPreferences.OsdPosition=="center"?.5:0);frame.RenderTransform=new ScaleTransform{ScaleX=systemPreferences.OsdScale/100d,ScaleY=systemPreferences.OsdScale/100d};scene.Children.Add(frame);paintOsdSample=frame;
        }
        else{var empty=Paint.Text("OSD 已关闭");empty.Foreground=Paint.Brush("FFFFFF");empty.HorizontalAlignment=HorizontalAlignment.Center;empty.VerticalAlignment=VerticalAlignment.Center;scene.Children.Add(empty);paintOsdSample=null;}
        var hint=Paint.Text("屏幕示意",12);hint.Foreground=Paint.Brush("B8FFFFFF");hint.HorizontalAlignment=HorizontalAlignment.Left;hint.VerticalAlignment=VerticalAlignment.Bottom;hint.Margin=new(12,0,0,10);scene.Children.Add(hint);var caption=Paint.Text(osdEnabled?(systemPreferences.OsdTheme=="cinema"?"电影字幕":systemPreferences.OsdTheme=="fade"?"向右羽化":"磨砂胶囊")+" · "+systemPreferences.OsdScale+"% · "+(systemPreferences.OsdPosition=="top-left"?"左上角":systemPreferences.OsdPosition=="center"?"屏幕正中":"下方中央"):"OSD 已关闭；重新开启后显示效果。",12,muted:true);
        return Paint.Stack(10,new Border{Child=scene,CornerRadius=new(12),BorderThickness=new(1),BorderBrush=Paint.Brush(Paint.Line(root))},caption);
    }
    private void PaintOsdReplay()
    {
        if(!osdEnabled||paintOsdSample==null||!paintOsdSample.IsLoaded||!Design.MotionEnabled)return;
        var visual=Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(paintOsdSample);var animation=visual.Compositor.CreateScalarKeyFrameAnimation();animation.Duration=TimeSpan.FromMilliseconds(MotionDesign.Duration(1500));animation.InsertKeyFrame(0,0);animation.InsertKeyFrame(.12f,1);animation.InsertKeyFrame(.9f,1);animation.InsertKeyFrame(1,0);visual.StartAnimation("Opacity",animation);
    }
    private async Task PaintReadPlans()
    {
        try{var result=await Task.Run(()=>{var api=new WindowsPowerPlans();return(Plans:api.List(),Active:api.Active());});paintPlans=result.Plans.ToArray();if(paintPlanChoice!=null)paintPlanChoice.Replace(paintPlans.Select(p=>p.Name).ToArray(),Array.FindIndex(paintPlans,p=>p.Id==result.Active));}
        catch(Exception error){actions.Add("电源方案列表："+error.Message);if(paintPlanChoice!=null)paintPlanChoice.IsEnabled=false;}
    }
    private async Task PaintSwitchPlan(PowerPlan requested)
    {
        if(uiReviewMode||upstream==null||settingsPowerBusy)return;settingsPowerBusy=true;
        if(!await operationQueue.EnterAsync()){settingsPowerBusy=false;return;}
        try{await Task.Run(()=>hardwareRuntime.Desktop.SelectPowerPlan(requested.Id));}
        finally{settingsPowerBusy=false;operationQueue.Release();await PaintReadPlans();}
    }
    private async Task PaintRecoverPlans()
    {
        if(uiReviewMode||upstream==null||!hardware.Active||settingsPowerBusy)return;if(!await Confirm("修复 Windows 电源计划？","先备份全部方案，再重建内置计划并尝试恢复OEM调度。自建方案会检查保留，定制的内置参数和睡眠策略可能重置。不会自动重启。"))return;
        settingsPowerBusy=true;
        if(!await operationQueue.EnterAsync()){settingsPowerBusy=false;return;}
        try{string result=await Task.Run(hardwareRuntime.Desktop.RecoverPowerPlans);await ShowMessage("电源计划恢复结果",result);}finally{settingsPowerBusy=false;operationQueue.Release();await PaintReadPlans();await ReadDesktopServices(true);}
    }
    private async Task PaintReadStartup(){if(uiReviewMode){paintStartup=false;return;}try{paintStartup=await Task.Run(StartupRegistration.IsEnabled);}catch{paintStartup=null;}RefreshPaintedState();}
    private async Task PaintTakeover(bool acquire)
    {
        if(uiReviewMode||takeoverChanging||closed)return;takeoverChanging=true;
        try{if(!acquire){await ReleaseHardwareControl();return;}await TakeoverHardware();}
        finally{takeoverChanging=false;SynchronizeModeKeyMonitor();RefreshPaintedState();}
    }
    private void RenderPaintedUpdates()
    {
        var check=PB("检查更新",async()=>{if(!uiReviewMode)await CheckNativeUpdate();});
        var install=PB("下载并安装",async()=>{if(!uiReviewMode)await DownloadNativeUpdate();});
        var cancel=PB(residentClient==null?"取消下载":"取消更新",async()=>
        {
            if(residentClient==null)updateCancellation?.Cancel();
            else await RunResidentUpdateActionAsync(PanelApplicationAction.CancelUpdate);
        });
        paintBindings.Add(()=>
        {
            check.IsEnabled=!PaintUpdateBusy&&(residentClient==null||residentSnapshot?.Ready==true&&!ResidentUpdateRequestBusy);
            install.IsEnabled=!PaintUpdateBusy&&(residentClient==null?availableUpdate!=null:
                ResidentUpdate is { CanInstall:true, AvailableVersion:not null }&&!ResidentUpdateRequestBusy);
            cancel.IsEnabled=residentClient==null?updateCancellation!=null:PaintUpdateBusy&&!PanelActionBusy(PanelOperation.Application,PanelApplicationAction.CancelUpdate);
        });
        page.Children.Add(PaintSection("软件更新","info",Paint.Stack(14,
            PT(()=>ResidentUpdate is { } state?$"v{state.CurrentVersion} · 社区版发布源":BuildInfo.UpdateDescription,12,muted:true),
            Paint.Inline(8,check,install,cancel),PT(()=>PaintUpdateMessage,12,muted:true),
            PB("发布页",()=>OpenSettings(ReleaseUpdater.ReleasesPage)),
            PB("打开下载与安装日志",async()=>
            {
                if(uiReviewMode)return;
                if(residentClient==null)OpenLocalFolder(Path.Combine(AppData.DirectoryPath,"updates"));
                else await RunResidentUpdateActionAsync(PanelApplicationAction.OpenUpdateDirectory);
            }))));
    }
}

