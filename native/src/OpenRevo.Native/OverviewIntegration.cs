using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core;
using OpenRevo.Core.Hal;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private OverviewSwitches extraSwitches=new(null,null,null,null);
    private OverviewTelemetryCoordinator overviewSampling => hardwareRuntime.Telemetry.Overview;
    private OverviewTelemetry? overviewTelemetry => residentClient != null ? residentOverviewTelemetry : overviewSampling.ReadState().Sample?.Data;
    private DateTimeOffset overviewTelemetryAt => (residentClient != null
        ? residentOverview?.At : overviewSampling.ReadState().Sample?.At)?.ToLocalTime() ?? default;
    private Task<OverviewSwitches>? extraSwitchRead;
    private int extraGeneration,extraReadGeneration;
    private DateTimeOffset lastExtraSwitchRead;
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
        var core = upstream;
        bool canRead = IsVisibleNow() && core != null && !gpuChanging;
        bool active = canRead && !mini && currentPage == "overview";
        bool readSwitches=canRead&&
            (active||mini||currentPage is "devices" or "minicustomization");
        if(extraSwitchRead is {IsCompleted:true})
        {
            if(extraReadGeneration==extraGeneration&&extraSwitchBusy.Count==0)
            {
                extraSwitches=extraSwitchRead.IsCompletedSuccessfully?extraSwitchRead.Result:new(null,null,null,null);
                UpdateMonochromeReadback(extraSwitches);
            }
            extraSwitchRead=null;UpdateExtraSwitchButtons();
        }
        if(readSwitches&&extraSwitchRead==null&&extraSwitchBusy.Count==0&&!quickBusy&&!hardwareBusy&&!monochromeBusy&&DateTimeOffset.Now-lastExtraSwitchRead>=TimeSpan.FromSeconds(6))
        {lastExtraSwitchRead=DateTimeOffset.Now;extraReadGeneration=extraGeneration;extraSwitchRead=Task.Run(()=>upstream!.ReadOverviewSwitches());}
        if(!active)
        {
            overviewSampling.SetForegroundDemand(null);
            return;
        }
        SampleOverviewPowerTargets();
        overviewSampling.SetForegroundDemand(core, CoreMachine.Current.DiscreteGpuDeclared, latest?.OnAc, ec.GpuFan);
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
            button.IsEnabled=!busy&&(key!="touchpad"||hardware.SupportsTouchpad)&&(key=="osd"||upstream!=null&&state.HasValue&&(key is not("fn" or "touchpad")||hardware.Active));
            if(!mini)UpdateApprovedQuickButton(button,key,state,busy);else SetChoiceVisual(button,state==true);
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
        try
        {
            extraSwitches=await hardwareRuntime.Switches.ToggleOverviewAsync(kind);
            uint feature=ExtraOsdKind(key);
            if(feature!=0&&extraSwitches.Get(kind) is bool confirmed)ConfirmShortcutOsd(feature,confirmed?1u:0u);
        }
        catch(Exception error)
        {
            try{extraSwitches=await Task.Run(hardwareRuntime.Switches.ReadOverview);}catch{extraSwitches=new(null,null,null,null);}
            actions.Add(error.Message);if(!closed)await ShowMessage("设置未完成",error.Message);
        }
        finally{lastExtraSwitchRead=DateTimeOffset.Now;extraSwitchBusy.Remove(key);if(!closed){UpdateExtraSwitchButtons();UpdateControlValues();}}
    }
    private void ObserveOsd(uint feature,uint value)
    {
        bool changed=observedOsd.TryGetValue(feature,out uint before)&&before!=value;observedOsd[feature]=value;
        if(changed&&osdEnabled)NotifyCoreOsd(feature,value);
    }
    private void ConfirmShortcutOsd(uint feature,uint value)
    {
        observedOsd[feature]=value;
        NotifyCoreOsd(feature,value);
    }
    private void NotifyCoreOsd(uint feature,uint value)
    {
        string? name=feature switch
        {
            1=>"power_mode",2=>"fan_boost",3=>"wifi",4=>"bluetooth",5=>"touchpad",
            6=>"camera",7=>"mic",8=>"win_key",9=>"fn_lock",10=>"keep_awake",_=>null
        };
        if(name!=null)PresentOsd(OsdMessage.FromFeature(name,value.ToString()));
    }
    private void PresentOsd(OsdMessage message)
    {
        if(closed||exitRequested||!osdEnabled||uiReviewMode)return;
        try
        {
            nativeOsd??=new NativeOsd(DispatcherQueue);
            nativeOsd.Configure(systemPreferences);
            nativeOsd.Show(message);
        }
        catch(Exception error)
        {
            OpenRevo.Core.DiagnosticLog.Failure("osd.present.failed",error,throttle:true);
        }
    }
    private void ShowNativeOsd(string title,string status)
        =>PresentOsd(new OsdMessage(title+" · "+status,"balance","#38BDF8"));
    private string lastActionOsd="";
    private DateTimeOffset lastActionOsdAt;
    private void ConfirmedActionOsd(string text,string icon)
    {
        if(closed||!osdEnabled||uiReviewMode)return;
        string key=icon+":"+text;
        if(lastActionOsd==key&&DateTimeOffset.Now-lastActionOsdAt<TimeSpan.FromMilliseconds(700))return;
        lastActionOsd=key;
        lastActionOsdAt=DateTimeOffset.Now;
        PresentOsd(new OsdMessage(text,icon,"#92B9FF"));
    }
    private void ObserveHardwareOsd()
    {
        if(hardware.CurrentMode is int mode&&mode is >=1 and <=4)ObserveOsd(1,(uint)mode);
        if((hardware.State?.FanBoost??ec.FanBoost)is bool boost)ObserveOsd(2,boost?1u:0u);
        foreach(var(feature,state)in new[]{(6u,quickState.Camera),(7u,quickState.Microphone),(8u,quickState.WinLocked),(10u,(bool?)(keepAwakeEnabled))})if(state.HasValue)ObserveOsd(feature,state.Value?1u:0u);
    }
}

