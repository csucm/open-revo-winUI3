using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private bool keepAwakeEnabled=>residentClient!=null?residentSnapshot?.KeepAwake==true:hardwareRuntime.SessionPower.KeepAwakeEnabled;
    private void RestoreScreenRequestOnInput()
    {
        try{hardwareRuntime.SessionPower.RestoreOnInput();}
        catch(Exception error){actions.Add("屏幕唤醒："+error.Message);}
    }
    private readonly Dictionary<string,Button> quickButtons=[];
    private sealed record QuickState(bool? Camera,bool CameraAvailable,bool? Microphone,bool? WinLocked)
    {
        internal CameraState? WindowsDevice { get; init; }
        internal int WinGeneration { get; init; }
    }
    private QuickState quickState=new(null,false,null,null);
    private Task<QuickState>? quickRead;
    private int quickGeneration,quickReadGeneration;
    private DateTimeOffset lastQuickRead;
    private bool quickBusy;
    private object? cameraWindowsDiagnostics;
    private QuickState ReadQuickState()
    {
        int winGeneration=winStateGeneration;
        return ToQuickState(hardwareRuntime.Switches.ReadShortcuts(),winGeneration);
    }
    private QuickState ToQuickState(DeviceShortcutRead result,int winGeneration)
    {
        cameraWindowsDiagnostics=result.CameraDiagnostics;
        return new(result.Camera,result.CameraAvailable,result.Microphone,result.WinLocked)
            {WindowsDevice=result.WindowsDevice,WinGeneration=winGeneration};
    }
    private void ApplyQuickRead(QuickState value)=>quickState=value with
    {WinLocked=value.WinGeneration==winStateGeneration&&requestedWinLocked==null?value.WinLocked:quickState.WinLocked};
    private void SampleQuickActions()
    {
        if(quickRead is {IsCompleted:true})
        {
            if(quickReadGeneration==quickGeneration&&!quickBusy)ApplyQuickRead(quickRead.IsCompletedSuccessfully?quickRead.Result:new(null,false,null,null){WinGeneration=winStateGeneration});
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
            "win"=>"Windows 软件锁；退出程序后解除，隐藏到托盘时保持",
            "awake"=>"保持电脑和屏幕唤醒；退出程序后自动解除",
            _=>"关闭屏幕显示，电脑继续运行；移动鼠标或按键唤醒"
        });
        quickButtons[key]=button;UpdateQuickActions();return button;
    }
    private void UpdateQuickActions()
    {
        foreach(var(key,button)in quickButtons)
        {
            bool selected=key switch{"camera"=>quickState.Camera==true,"mic"=>quickState.Microphone==true,"win"=>quickState.WinLocked==true,"awake"=>keepAwakeEnabled,_=>false};
            string label=key switch
            {
                "camera"=>quickState.Camera switch{true=>"相机已开",false=>"相机已关",_=>"相机未知"},
                "mic"=>quickState.Microphone switch{true=>"麦克风",false=>"已静音",_=>"麦克风未知"},
                "win"=>requestedWinLocked.HasValue?"切换中":quickState.WinLocked switch{true=>"已锁定",false=>"已解锁",_=>"Win键锁"},
                "awake"=>keepAwakeEnabled?"防休眠中":"防休眠",_=>"关闭屏幕"
            };
            string glyph=key switch{"camera"=>quickState.Camera==true?"":"","mic"=>"","win"=>"","awake"=>"",_=>""};
            if(mini&&!Equals(button.Tag,label))
            {
                var content=new StackPanel{Spacing=3,HorizontalAlignment=HorizontalAlignment.Center};
                content.Children.Add(Ui.Icon(glyph,mini?17:20));content.Children.Add(Ui.Text(label,mini?10:12));button.Content=content;button.Tag=label;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button,label);
            }
            button.IsEnabled=key switch{"camera"=>hardware.Active&&quickState.CameraAvailable&&quickState.Camera.HasValue&&upstream!=null,"mic"=>quickState.Microphone.HasValue&&upstream!=null,"win"=>upstream!=null&&quickState.WinLocked.HasValue,_=>true};
            if(!mini){UpdateApprovedQuickButton(button,key,key switch{"camera"=>quickState.Camera,"mic"=>quickState.Microphone,"win"=>quickState.WinLocked,"awake"=>keepAwakeEnabled,_=>false},quickBusy);continue;}
            if(selected)button.Style=(Style)Application.Current.Resources["SelectedChoiceStyle"];else button.ClearValue(FrameworkElement.StyleProperty);
        }
    }
    private async Task RunQuickAction(string key)
    {
        if(residentClient!=null){await RunResidentQuickActionAsync(key);return;}
        if(key=="win"){await RequestWinToggle();return;}
        if(quickBusy||closed||exitRequested)return;
        bool needsQueue=key is "awake" or "screen";
        if(needsQueue&&!await operationQueue.EnterAsync())return;
        quickBusy=true;
        quickGeneration++;
        try
        {
            switch(key)
            {
                case "camera":
                    bool delegated=hardware.CoreDelegated;
                    ApplyQuickRead(await ToggleCameraThroughCore());
                    if(!delegated&&quickState.Camera is bool camera)ConfirmShortcutOsd(6,camera?1u:0u);
                    break;
                case "mic":
                    bool mic=await hardwareRuntime.Switches.ToggleMicrophoneAsync();
                    quickState=quickState with{Microphone=mic};ConfirmShortcutOsd(7,mic?1u:0u);break;
                case "awake":
                    ConfirmShortcutOsd(10,hardwareRuntime.SessionPower.ToggleKeepAwake()?1u:0u);break;
                case "screen":
                    if(await hardwareRuntime.SessionPower.TurnOffAsync(captionWindow)&&!closed)
                        ConfirmedActionOsd("息屏请求已发出","display");
                    break;
            }
        }
        catch(System.ComponentModel.Win32Exception error) when(error.NativeErrorCode==1223){if(!closed)await ShowMessage("已取消","没有更改相机设置。");}
        catch(Exception error){actions.Add(error.ToString());if(!closed)await ShowMessage("设置未完成",error.Message);}
        finally{quickBusy=false;lastQuickRead=DateTimeOffset.Now;if(needsQueue)operationQueue.Release();if(!closed){UpdateQuickActions();UpdateControlValues();}}
    }
    private async Task<QuickState> ToggleCameraThroughCore()
    {
        int generation=winStateGeneration;
        var result=await hardwareRuntime.Switches.ToggleCameraAsync(quickState.WindowsDevice);
        return ToQuickState(result,generation);
    }
}


