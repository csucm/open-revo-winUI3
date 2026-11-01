using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private SessionPower? sessionPower;
    private SessionPower? screenOffPower;
    private uint screenOffInput;
    private bool screenOffPending;
    private void RestoreScreenRequestOnInput()
    {
        if(screenOffPower==null||screenOffPending)return;
        try
        {
            if(SessionPower.ReadLastInputTick()==screenOffInput)return;
            screenOffPower.Dispose();screenOffPower=null;
            if(sessionPower?.Enabled==true)sessionPower.SetKeepAwake(true);
        }
        catch(Exception error){actions.Add("屏幕唤醒："+error.Message);}
    }
    private readonly Dictionary<string,Button> quickButtons=[];
    private sealed record QuickState(bool? Camera,bool CameraAvailable,bool? Microphone,bool? WinLocked);
    private QuickState quickState=new(null,false,null,null);
    private Task<QuickState>? quickRead;
    private int quickGeneration,quickReadGeneration;
    private DateTimeOffset lastQuickRead;
    private bool quickBusy;
    private QuickState ReadQuickState()
    {
        bool? camera=null,mic=null,win=null;bool available=false;CameraState? pnp=null;
        try {pnp=CameraControl.Read();available=pnp!=null;}catch{}
        try {if(hardware.Active){var state=hardware.ProbeSwitches();win=state.WinKeyLocked;camera=state.CameraPowered?pnp?.Enabled:false;}}catch{}
        try {mic=upstream?.ReadMicrophone();}catch{}
        try {if(camera==true && upstream?.IsCameraAccessBlocked()==true)camera=false;}catch{camera=null;}
        return new(camera,available,mic,win);
    }
    private void SampleQuickActions()
    {
        if(quickRead is {IsCompleted:true})
        {
            if(quickReadGeneration==quickGeneration&&!quickBusy)quickState=quickRead.IsCompletedSuccessfully?quickRead.Result:new(null,false,null,null);
            quickRead=null;UpdateQuickActions();
        }
        if(!quickBusy && requestedWinLocked==null && !hardwareBusy && !gpuChanging && quickRead==null && DateTimeOffset.Now-lastQuickRead>TimeSpan.FromSeconds(6))
        {lastQuickRead=DateTimeOffset.Now;quickReadGeneration=quickGeneration;quickRead=Task.Run(ReadQuickState);}
    }
    private Button QuickActionButton(string key)
    {
        var button=Ui.Button("",async(_,_)=>await RunQuickAction(key));
        ToolTipService.SetToolTip(button,key switch
        {
            "camera"=>"开启或关闭笔记本摄像头；被 Windows 禁用时需先启用设备",
            "mic"=>"静音或恢复 Windows 默认麦克风",
            "win"=>"锁定 Win 键，避免游戏时误触；再次点击解锁",
            "awake"=>"保持电脑和屏幕唤醒；退出程序后自动解除",
            _=>"关闭屏幕显示，电脑继续运行；移动鼠标或按键唤醒"
        });
        quickButtons[key]=button;UpdateQuickActions();return button;
    }
    private void UpdateQuickActions()
    {
        foreach(var(key,button)in quickButtons)
        {
            bool selected=key switch{"camera"=>quickState.Camera==true,"mic"=>quickState.Microphone==true,"win"=>quickState.WinLocked==true,"awake"=>sessionPower?.Enabled==true,_=>false};
            string label=key switch
            {
                "camera"=>quickState.Camera switch{true=>"相机已开",false=>"相机已关",_=>"相机未知"},
                "mic"=>quickState.Microphone switch{true=>"麦克风",false=>"已静音",_=>"麦克风未知"},
                "win"=>requestedWinLocked.HasValue?"切换中":quickState.WinLocked switch{true=>"已锁定",false=>"已解锁",_=>"Win键锁"},
                "awake"=>sessionPower?.Enabled==true?"防休眠中":"防休眠",_=>"关闭屏幕"
            };
            string glyph=key switch{"camera"=>quickState.Camera==true?"":"","mic"=>"","win"=>"","awake"=>"",_=>""};
            if(!Equals(button.Tag,label))
            {
                var content=new StackPanel{Spacing=3,HorizontalAlignment=HorizontalAlignment.Center};
                content.Children.Add(Ui.Icon(glyph,mini?17:20));content.Children.Add(Ui.Text(label,mini?10:12));button.Content=content;button.Tag=label;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button,label);
            }
            button.IsEnabled=key switch{"camera"=>hardware.Active&&quickState.CameraAvailable&&quickState.Camera.HasValue&&upstream!=null,"mic"=>quickState.Microphone.HasValue&&upstream!=null,"win"=>hardware.Active&&quickState.WinLocked.HasValue,_=>true};
            if(selected)button.Style=(Style)Application.Current.Resources["SelectedChoiceStyle"];else button.ClearValue(FrameworkElement.StyleProperty);
        }
    }
    private async Task RunQuickAction(string key)
    {
        if(key=="win"){await RequestWinToggle();return;}
        if(quickBusy||closed)return;
        await operationQueue.WaitAsync();
        if(closed){operationQueue.Release();return;}
        quickBusy=true;
        quickGeneration++;
        try
        {
            switch(key)
            {
                case "camera":
                    quickState=await ToggleCameraThroughCore();break;
                case "mic":
                    bool mic=await Task.Run(()=>{var value=upstream?.ReadMicrophone()??throw new IOException("未找到默认麦克风。");upstream!.SetMicrophone(!value);return !value;});quickState=quickState with{Microphone=mic};break;
                case "awake":
                    sessionPower??=new SessionPower();sessionPower.SetKeepAwake(!sessionPower.Enabled);break;
                case "screen":
                    screenOffPending=true;
                    try
                    {
                        sessionPower?.ReleaseDisplay();
                        screenOffPower??=new SessionPower();screenOffPower.HoldSystemOnly();await Task.Delay(350);
                        if(!closed)
                        {
                            screenOffInput=SessionPower.ReadLastInputTick();
                            SessionPower.TurnOffScreen(WinRT.Interop.WindowNative.GetWindowHandle(this));
                        }
                    }
                    finally{screenOffPending=false;}
                    break;
            }
        }
        catch(System.ComponentModel.Win32Exception error) when(error.NativeErrorCode==1223){if(!closed)await ShowMessage("已取消","没有更改相机设置。");}
        catch(Exception error){actions.Add(error.ToString());if(!closed)await ShowMessage("设置未完成",error.Message);}
        finally{quickBusy=false;lastQuickRead=DateTimeOffset.Now;operationQueue.Release();if(!closed){UpdateQuickActions();UpdateControlValues();}}
    }
    private async Task<QuickState> ToggleCameraThroughCore()
    {
        if(!hardware.Active||upstream==null)throw new InvalidOperationException("请先连接硬件。");
        var current=await Task.Run(ReadQuickState);
        if(!current.Camera.HasValue)throw new IOException("无法确认相机状态，请检查设备管理器。");
        bool enable=!current.Camera.Value,completed=false;
        bool beforePowered=(await Task.Run(hardware.ProbeSwitches)).CameraPowered;
        try
        {
            await Task.Run(()=>hardware.SetCameraPoweredViaCore(upstream,enable));
            if(enable)
            {
                var pnp=await Task.Run(()=>
                {
                    CameraState? value=null;
                    for(int i=0;i<30;i++)
                    {
                        value=CameraControl.Read();
                        if(value?.Enabled.HasValue==true)return value;
                        Thread.Sleep(100);
                    }
                    return value;
                });
                // The normal switch only changes the upstream hardware power bit.
                // Repair a legacy Windows-disabled device once; do not disable it
                // on every OFF action and demand elevation again at the next ON.
                if(pnp?.Enabled==false || upstream.IsCameraAccessBlocked())await CameraControl.Request(true);
                else if(pnp?.Enabled!=true)throw new IOException("相机尚未就绪，请稍后重试。");
            }
            var confirmed=await Task.Run(ReadQuickState);
            if(confirmed.Camera!=enable)throw new IOException("相机状态未确认改变。");
            completed=true;
            return confirmed;
        }
        finally
        {
            if(!completed && !beforePowered && hardware.Active)
                await Task.Run(()=>{if(hardware.ProbeSwitches().CameraPowered)hardware.SetCameraPoweredViaCore(upstream,false);});
        }
    }
}
