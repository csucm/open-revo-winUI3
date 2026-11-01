using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private OverviewSwitches extraSwitches=new(null,null,null,null);
    private OverviewTelemetry? overviewTelemetry;
    private Task<OverviewTelemetry>? overviewTelemetryRead;
    private Task<OverviewSwitches>? extraSwitchRead;
    private int extraGeneration,extraReadGeneration;
    private DateTimeOffset lastOverviewTelemetry,lastExtraSwitchRead,overviewTelemetryAt;
    private readonly Dictionary<string,Button> extraSwitchButtons=[];
    private readonly HashSet<string> extraSwitchBusy=[];
    private ProgressBar? overviewGpuBar,overviewVramBar;
    private bool osdEnabled=true;
    private NativeOsd? nativeOsd;
    private readonly Dictionary<uint,uint> observedOsd=[];
    private static OverviewSwitch ExtraSwitchKind(string key)=>key switch{"wifi"=>OverviewSwitch.Wifi,"bluetooth"=>OverviewSwitch.Bluetooth,"touchpad"=>OverviewSwitch.Touchpad,"fn"=>OverviewSwitch.FnLock,_=>throw new ArgumentOutOfRangeException(nameof(key))};
    private static uint ExtraOsdKind(string key)=>key switch{"wifi"=>3u,"bluetooth"=>4u,"touchpad"=>5u,"fn"=>9u,_=>0u};
    private void SampleOverviewIntegration()
    {
        bool active=!mini&&currentPage=="overview"&&IsVisibleNow()&&upstream!=null&&!gpuChanging;
        if(overviewTelemetryRead is {IsCompleted:true})
        {
            overviewTelemetry=active&&overviewTelemetryRead.IsCompletedSuccessfully?overviewTelemetryRead.Result:null;
            overviewTelemetryAt=lastOverviewTelemetry;overviewTelemetryRead=null;UpdateOverviewValues();
        }
        if(extraSwitchRead is {IsCompleted:true})
        {
            if(extraReadGeneration==extraGeneration&&extraSwitchBusy.Count==0)extraSwitches=extraSwitchRead.IsCompletedSuccessfully?extraSwitchRead.Result:new(null,null,null,null);
            extraSwitchRead=null;UpdateExtraSwitchButtons();
        }
        if(!active)return;
        SampleOverviewPowerTargets();
        if(overviewTelemetryRead==null&&DateTimeOffset.Now-lastOverviewTelemetry>=TimeSpan.FromSeconds(2))
        {
            lastOverviewTelemetry=DateTimeOffset.Now;bool gpu=latest?.OnAc==true&&ec.GpuFan is >0;
            overviewTelemetryRead=Task.Run(()=>upstream!.ReadOverviewTelemetry(gpu));
        }
        if(extraSwitchRead==null&&extraSwitchBusy.Count==0&&!quickBusy&&!hardwareBusy&&DateTimeOffset.Now-lastExtraSwitchRead>=TimeSpan.FromSeconds(6))
        {lastExtraSwitchRead=DateTimeOffset.Now;extraReadGeneration=extraGeneration;extraSwitchRead=Task.Run(()=>upstream!.ReadOverviewSwitches());}
        UpdateOverviewValues();
    }
    private Button ExtraSwitchButton(string key,string title,string glyph)
    {
        Button button=Ui.Button("",async(_,_)=>
        {
            if(key=="osd")
            {
                osdEnabled=!osdEnabled;UpdateExtraSwitchButtons();await SavePreferences();
                if(osdEnabled)ShowNativeOsd("屏幕提示","已开启");else nativeOsd?.Hide();return;
            }
            await SetExtraSwitch(key);
        });
        var body=Ui.Stack(DetailGlyph(glyph,22),Ui.Text(title,12));body.Spacing=5;body.HorizontalAlignment=HorizontalAlignment.Center;button.Content=body;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button,title);
        ToolTipService.SetToolTip(button,key switch{"wifi"=>"开启或关闭 WiFi 无线电","bluetooth"=>"开启或关闭蓝牙无线电","touchpad"=>"切换Windows触摸板与固件状态","fn"=>"切换固件 Fn Lock（F1–F12 模式）",_=>"显示性能、散热和设备开关的屏幕提示"});
        extraSwitchButtons[key]=button;return button;
    }
    private void UpdateExtraSwitchButtons()
    {
        foreach(var(key,button)in extraSwitchButtons)
        {
            bool? state=key=="osd"?osdEnabled:extraSwitches.Get(ExtraSwitchKind(key));
            bool busy=extraSwitchBusy.Contains(key);
            button.IsEnabled=!busy&&(key=="osd"||upstream!=null&&state.HasValue&&(key is not("fn" or "touchpad")||hardware.Active));
            SetChoiceVisual(button,state==true);
            if(button.Content is StackPanel body)
            {
                while(body.Children.Count>2)body.Children.RemoveAt(2);
                if(busy||!state.HasValue)body.Children.Add(Ui.Text(busy?"切换中":"不可用",10,muted:true));
            }
            if(key!="osd"&&state.HasValue)ObserveOsd(ExtraOsdKind(key),state.Value?1u:0u);
        }
    }
    private async Task SetExtraSwitch(string key)
    {
        if(closed||upstream==null||extraSwitchBusy.Contains(key))return;
        var kind=ExtraSwitchKind(key);extraGeneration++;extraSwitchBusy.Add(key);UpdateExtraSwitchButtons();
        await operationQueue.WaitAsync();
        if(closed){extraSwitchBusy.Remove(key);operationQueue.Release();return;}
        quickBusy=true;
        try
        {
            extraSwitches=await Task.Run(upstream.ReadOverviewSwitches);
            bool before=extraSwitches.Get(kind)??throw new IOException("设备当前状态不可用。");
            extraSwitches=await Task.Run(()=>
            {
                if(kind is OverviewSwitch.Touchpad or OverviewSwitch.FnLock)return hardware.SetOverviewSwitchViaCore(upstream,kind,before,!before);
                upstream.SetOverviewSwitch(kind,before,!before);return upstream.ReadOverviewSwitches();
            });
            if(extraSwitches.Get(kind)!=!before)throw new IOException("设备回读未确认改变。");
        }
        catch(Exception error)
        {
            try{extraSwitches=await Task.Run(upstream.ReadOverviewSwitches);}catch{extraSwitches=new(null,null,null,null);}
            actions.Add(error.Message);if(!closed)await ShowMessage("设置未完成",error.Message);
        }
        finally{quickBusy=false;lastExtraSwitchRead=DateTimeOffset.Now;extraSwitchBusy.Remove(key);operationQueue.Release();if(!closed){UpdateExtraSwitchButtons();UpdateControlValues();}}
    }
    private void ObserveOsd(uint feature,uint value)
    {
        bool changed=observedOsd.TryGetValue(feature,out uint before)&&before!=value;observedOsd[feature]=value;
        if(changed&&osdEnabled)_=NotifyCoreOsd(feature,value);
    }
    private async Task NotifyCoreOsd(uint feature,uint value)
    {
        if(closed||upstream==null||!osdEnabled||uiReviewMode)return;
        try
        {
            var item=await Task.Run(()=>upstream.SendOsd(feature,value));
            if(item==null||closed||!osdEnabled)return;
            nativeOsd??=new NativeOsd(DispatcherQueue);nativeOsd.Configure(systemPreferences);nativeOsd.ShowFeature(item.Feature,item.Value);
        }
        catch(Exception error){actions.Add("OSD: "+error.Message);}
    }
    private void ShowNativeOsd(string title,string status)
    {if(closed||!osdEnabled)return;nativeOsd??=new NativeOsd(DispatcherQueue);nativeOsd.Configure(systemPreferences);nativeOsd.Show(title,status);}
    private void ObserveHardwareOsd()
    {
        if(hardware.CurrentMode is int mode&&mode is >=1 and <=4)ObserveOsd(1,(uint)mode);
        if((hardware.State?.FanBoost??ec.FanBoost)is bool boost)ObserveOsd(2,boost?1u:0u);
        foreach(var(feature,state)in new[]{(6u,quickState.Camera),(7u,quickState.Microphone),(8u,quickState.WinLocked),(10u,(bool?)(sessionPower?.Enabled==true))})if(state.HasValue)ObserveOsd(feature,state.Value?1u:0u);
    }
}
