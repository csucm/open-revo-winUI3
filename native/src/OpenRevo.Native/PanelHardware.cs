using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core;
using OpenRevo.Core.Hal;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private UpstreamCoreLibrary? upstream => hardwareRuntime.Bridge;
    private KeyboardCapability keyboard = KeyboardCapability.Detect(false, false, false);
    private TemperatureSensors temperatures => hardwareRuntime.Telemetry.Temperatures;
    private Task<TemperatureReading>? temperatureTask;
    private Task? gpuTemperatureRelease;
    private bool gpuTemperatureDemand, acceptGpuTemperatureSample;
    private TemperatureReading temperature = new(null, null);
    private DateTimeOffset lastTemperature;
    private bool gpuChanging;
    private bool gpuReadFresh => residentClient!=null?residentSnapshot?.Gpu.Fresh==true:hardwareRuntime.Gpu.Snapshot.Fresh;
    private bool UsesDeclaredGpuMux => residentClient!=null?residentSnapshot?.Gpu.Declared==true:hardware.CoreDelegated && CoreMachine.Current.Manifest is not (null or "");
    private GpuBootState? gpuBoot => hardwareRuntime.Gpu.Snapshot.Boot;
    private GpuRuntimeCapabilities? gpuRuntime => hardwareRuntime.Gpu.Snapshot.Runtime;
    private int GpuRuntimeMode => residentClient!=null?residentSnapshot?.Gpu.RuntimeMode??0:hardwareRuntime.Gpu.Snapshot.RuntimeMode;
    private bool GpuRestartPending => residentClient!=null?residentSnapshot?.Gpu.RestartPending==true:hardwareRuntime.Gpu.Snapshot.RestartPending;
    private string gpuModeLabel = "状态待读取";
    private readonly Dictionary<int, Button> gpuButtons = [];
    private GpuModeState? gpuState => residentClient==null?hardwareRuntime.Gpu.Snapshot.Firmware:
        residentSnapshot?.Gpu is { Fresh: true } state?new(state.FirmwareMode,state.FirmwareMode>0,state.Integrated,state.Hybrid,state.Discrete):null;
    private Button? gpuReadButton;
    private async Task LoadPanelHardware()
    {
        lightingReadFresh=false;lightingProbeError=null;
        try
        {
            hardwareRuntime.OpenBridge();
            if(quickState.WinLocked==null)await RefreshWindowsWinLock();
            await ReadKeyboardLightingState();
            if (EcReader.IsAllowed(identity))
            {
                var result=await hardwareRuntime.Gpu.RefreshAsync(persist:!uiReviewMode);
                if(result.Error is { } error)actions.Add("显卡读取："+error.Message);
                gpuModeLabel=gpuReadFresh?OriginalGpuName(gpuState?.Mode??0):"待读取";
            }
            if (GpuRestartPending) gpuModeLabel = "待重启";
        }
        catch (Exception error) { DiagnosticLog.Failure("hardware.panel_initialization",error,new{keyboard.Kind,lightingReadFresh});actions.Add("Core 识别：" + error.Message); }
        await ReadOverviewPowerTargets();
        SynchronizeModeKeyMonitor();
    }
    // Query-only shared path. It neither acquires hardware nor reapplies settings.
    private async Task ReadKeyboardLightingState()
    {
        lightingReadFresh=false; lightingProbeError=null;
        hardwareRuntime.OpenBridge();
        var result = await Task.Run(() => hardwareRuntime.Desktop.ReadKeyboardLighting(identity.Board));
        keyboard = result.Capability;
        lightingProbeError = result.Error;
        if (keyboard.Kind == "White")
        {
            monochromeLevels = result.MonochromeLevels;
            if (result.Switches is { } switches)
            {
                extraSwitches = switches;
                UpdateMonochromeReadback(switches);
            }
        }
        else if (result.CommandOnly)
            lightingProbeError = "可发送 RGB 灯效和颜色命令，当前状态未知。";
        if (result.Confirmed is not { } light) return;
        lightingReadFresh = true;
        rgbLevel = (light.Backlight & 2) != 0 ? 0 : Math.Clamp(light.Backlight >> 5, 0, 4);
        rgbMode = rgbLevel == 0 ? "关闭背光" : (light.Trigger & Vault.RainbowMask) != 0 ? "彩虹循环" : "静态单色";
        // A nearest named preset is a label. Keep the actual color readback.
        uint Channel(int value) => (uint)Math.Clamp((int)Math.Round(value * 255d / 50), 0, 255);
        customRgb = Channel(light.Red) << 16 | Channel(light.Green) << 8 | Channel(light.Blue);
        rgbColor = colorPresets.MinBy(x => Math.Pow((x.Hex >> 16) - light.Red * 255d / 50, 2)
            + Math.Pow(((x.Hex >> 8) & 255) - light.Green * 255d / 50, 2)
            + Math.Pow((x.Hex & 255) - light.Blue * 255d / 50, 2)).Name;
    }
    private FrameworkElement GpuChoices()
    {
        if(!mini)return DesktopGpuChoices();
        gpuButtons.Clear();
        Button Mode(int value)
        {
            var definition=originalGpuModes.Single(m=>m.Mode==value);
            var button=Ui.Button(definition.Title,async(_,_)=>await RunGpuRequest(value));
            var content=Ui.Stack(ApprovedIcons.Create(value==1?"gpu-integrated":value==2?"gpu-hybrid":"gpu-discrete",28,true,true),Ui.Text(definition.Title,12));content.Spacing=2;content.HorizontalAlignment=HorizontalAlignment.Center;
            button.Content=content;button.Padding=new Thickness(3);button.Height=56;
            button.IsEnabled=hardware.Active&&upstream!=null&&!gpuChanging&&gpuReadFresh&&gpuState?.Supports(value)==true;
            if(gpuState?.Mode==value)button.Style=(Style)Application.Current.Resources["SelectedChoiceStyle"];
            ToolTipService.SetToolTip(button,definition.Description+"\n保存下次启动设置；支持情况以硬件回读为准。");gpuButtons[value]=button;return button;
        }
        // Keep the approved mini order, columns and 56-DIP button height.
        return Columns(Mode(1),Mode(2),Mode(3));
    }

    private async Task RunGpuRequest(int target)
    {
        if(residentClient!=null){await RunResidentGpuAsync(target);return;}
        if(closed||gpuChanging||(target!=0&&(!hardware.Active||!gpuReadFresh))||upstream==null)return;
        int expected=gpuState?.Mode??0;
        gpuChanging=true;UpdateGpuControls();
        try
        {
            UpdateGpuTemperatureDemand();
            if(ecTask!=null)try{await ecTask;}catch{}
            if(temperatureTask!=null)try{await temperatureTask;}catch{}
            if (gpuTemperatureRelease != null) await gpuTemperatureRelease;
            var reply=target==0?await hardwareRuntime.Gpu.RefreshAsync():await hardwareRuntime.Gpu.SelectAsync(target,expected);
            if(closed)return;
            if(reply.Outcome==GpuCommandOutcome.CommandSent)
            {
                ShowHardwareSuccess("Core 已发送显卡模式命令。请刷新读取状态。");
                return;
            }
            if(reply.Error is { } failure)
            {
                actions.Add(failure.ToString());
                if(!exitRequested)await GpuMessage("显卡设置未完成",failure.Message);
                return;
            }
            if(reply.Outcome==GpuCommandOutcome.ReadbackConfirmed)
            {
                ConfirmedActionOsd(OriginalGpuName(target)+(GpuRestartPending?" · 待重启":" · 已生效，无需重启"),target==1?"gpu-integrated":target==2?"gpu-hybrid":"gpu-discrete");
                await GpuMessage(GpuRestartPending?"显卡模式已保存":"显卡设置已确认",GpuRestartPending?$"已选择{OriginalGpuName(target)}，重启后生效。准备好后请自行重启电脑。":"已确认当前显卡模式，无需为此次切换重启。");
            }
            else if(reply.Outcome==GpuCommandOutcome.NoChange&&!exitRequested)
                await GpuMessage("无需更改",GpuRestartPending?"此模式已经保存，重启后生效。":"固件已设置为此模式。");
        }
        catch(System.ComponentModel.Win32Exception error) when(error.NativeErrorCode==1223){if(!closed)await GpuMessage("已取消","没有执行显卡操作。");}
        catch(Exception error){actions.Add(error.ToString());if(!closed&&!exitRequested)await GpuMessage("显卡结果未确认","请点击显卡模式标题重新读取设置。");}
        finally{gpuChanging=false;lastEc=DateTimeOffset.MinValue;if(!closed)UpdateGpuControls();}
    }
    private Task GpuMessage(string title,string message)
    {
        if(Environment.GetCommandLineArgs().Contains("--ui-gpu-checks")){actions.Add(title+"："+message);return Task.CompletedTask;}
        return ShowMessage(title,message);
    }
    private void UpdateGpuControls()
    {
        RefreshPaintedState();
        gpuModeLabel=!gpuReadFresh?"待读取":GpuRestartPending?"待重启":gpuState?.Known==true?OriginalGpuName(gpuState.Mode):"待读取";
        Put("gpuTitle","当前运行："+OriginalGpuName(GpuRuntimeMode)+(GpuRestartPending?" · 目标："+OriginalGpuName(gpuState?.Mode??0)+" · 待重启":" · 已生效"));
        if(gpuReadButton!=null)
        {
            gpuReadButton.IsEnabled=EcReader.IsAllowed(identity)&&!gpuChanging;
            string note=gpuRuntime?.Description(gpuBoot?.InitialMode??0)??"热切换能力待读取";
            ToolTipService.SetToolTip(gpuReadButton,(GpuRestartPending?$"已保存{OriginalGpuName(gpuState?.Mode??0)}，重启后生效；点击刷新读取":"点击读取或刷新固件显卡设置")+"\n"+note);
        }
        int selected=gpuState?.Mode??0;
        foreach(var(value,button)in gpuButtons)
        {
            button.IsEnabled=hardware.Active&&upstream!=null&&!gpuChanging&&gpuReadFresh&&gpuState?.Supports(value)==true;
            SetChoiceVisual(button,value==selected);
        }
    }
    private void UpdateGpuTemperatureDemand()
    {
        // Large overview and tray GPU temperature already come from the Rust snapshot.
        bool needed = mini && IsVisibleNow() && !trayHidden && !gpuChanging &&
            !PaintIntegratedOnly && MiniSaved.Top.Contains("gpu") &&
            latest?.OnAc == true && ec.GpuFan is > 0;
        if (needed == gpuTemperatureDemand) return;
        gpuTemperatureDemand = needed;
        if (needed)
        {
            lastTemperature = DateTimeOffset.MinValue;
            return;
        }

        temperature = temperature with { Gpu = null };
        acceptGpuTemperatureSample = false;
        if (gpuTemperatureRelease is { IsCompleted: false }) return;
        var pending = temperatureTask;
        gpuTemperatureRelease = Task.Run(async () =>
        {
            // A queued read can still start after demand was removed.
            if (pending != null)
            {
                try { await pending.ConfigureAwait(false); }
                catch { /* The tick owns the sample result. */ }
            }
            try { temperatures.ReleaseGpu(); }
            catch (Exception error) { DiagnosticLog.Failure("temperature.gpu_release", error); }
        });
    }

    private void SamplePanelTemperatures()
    {
        if (temperatureTask is { IsCompleted:true })
        {
            var reading = temperatureTask.IsCompletedSuccessfully ? temperatureTask.Result : new(null, null);
            temperature = acceptGpuTemperatureSample ? reading : reading with { Gpu = null };
            temperatureTask = null;
        }
        if (!gpuChanging && temperatureTask == null && gpuTemperatureRelease is not { IsCompleted: false } &&
            DateTimeOffset.Now-lastTemperature >= TimeSpan.FromSeconds(6))
        {
            lastTemperature=DateTimeOffset.Now;
            bool sampleGpu = gpuTemperatureDemand;
            acceptGpuTemperatureSample = sampleGpu;
            var core=upstream;
            temperatureTask=Task.Run(()=>{
                var reading=temperatures.Read(sampleGpu);
                try
                {
                    var live=core?.Read(24);
                    if(live is {ValueKind:System.Text.Json.JsonValueKind.Number} value&&value.TryGetInt32(out int cpu))
                        return reading with{Cpu=cpu,CpuSource="Core 硬件温度"};
                }
                catch(Exception error){DiagnosticLog.Failure("temperature.core",error,throttle:true);}
                return reading;
            });
        }
        string Temp(int? value) => value is int n ? $"{n}°C" : "—";
        string Fan(int? value) => value is int n ? $"fans: {n} RPM" : "fans: — RPM";
        Put("miniCpu",$"CPU {Temp(temperature.Cpu)} · {Fan(ec.CpuFan)}");
        Put("miniGpu",$"GPU {Temp(temperature.Gpu)} · {Fan(ec.GpuFan)}");
        Put("cpuTemperature",$"{temperature.CpuSource} {Temp(temperature.Cpu)}");
        Put("gpuTemperature",temperature.Gpu.HasValue ? Temp(temperature.Gpu) : "休眠或暂不可用");
    }
}




