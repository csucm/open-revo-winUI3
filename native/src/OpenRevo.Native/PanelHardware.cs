using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core;
using OpenRevo.Core.Hal;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private UpstreamCoreLibrary? upstream;
    private KeyboardCapability keyboard = KeyboardCapability.Detect(false, false, false);
    private readonly TemperatureSensors temperatures = new();
    private Task<TemperatureReading>? temperatureTask;
    private TemperatureReading temperature = new(null, null);
    private DateTimeOffset lastTemperature;
    private int requestedGpu;
    private long gpuSavedAt;
    private bool gpuChanging;
    private bool gpuReadFresh;
    private bool UsesDeclaredGpuMux => hardware.CoreDelegated && CoreMachine.Current.Manifest is not (null or "");
    private GpuBootState? gpuBoot;
    private GpuRuntimeCapabilities? gpuRuntime;
    private int GpuRuntimeMode=>gpuBoot?.InitialMode==3?3:gpuRuntime is {HybridIgpuSupported:true,IgpuActive:bool active}?(active?1:2):gpuBoot?.InitialMode??0;
    private bool GpuRestartPending=>GpuRuntimeMode is >=1 and <=3&&gpuState?.Known==true?gpuState.Mode!=GpuRuntimeMode:gpuBoot?.Pending==true;
    private string gpuModeLabel = "状态待读取";
    private readonly Dictionary<int, Button> gpuButtons = [];
    private GpuModeState? gpuState;
    private Button? gpuReadButton;
    private async Task LoadPanelHardware()
    {
        lightingReadFresh=false;lightingProbeError=null;
        try
        {
            upstream ??= new UpstreamCoreLibrary(Path.Combine(AppContext.BaseDirectory, "openrevo_native_bridge.dll"));
            if(quickState.WinLocked==null)await RefreshWindowsWinLock();
            var caps = await Task.Run(() => upstream.Read(5));
            string? modelHint = null;
            var catalog = await Task.Run(() => upstream.Read(6));
            foreach (var model in catalog.EnumerateArray())
                if (model.GetProperty("model_id").GetString()?.Equals(identity.Board,StringComparison.OrdinalIgnoreCase)==true && model.TryGetProperty("keyboard_type",out var hint))
                    modelHint=hint.GetString();
            keyboard = KeyboardCapability.Detect(caps.GetProperty("per_key").GetBoolean(), caps.GetProperty("four_zone").GetBoolean(),
                hardware.State is { } state && (state.Support2 & Vault.SingleZoneSupportMask) != 0, modelHint);
            if(hardware.CoreDelegated && CoreMachine.Current.MonochromeLevels is { } levels)
            {
                keyboard=KeyboardCapability.Detect(false,false,false,"White");
                monochromeLevels=levels;
                extraSwitches=await Task.Run(upstream.ReadOverviewSwitches);
                UpdateMonochromeReadback(extraSwitches);
            }
            else if(hardware.SupportsDeclaredRgb)
            {
                keyboard=new("SingleZone","单区 RGB",true,false);
                lightingProbeError="可发送 RGB 灯效和颜色命令，当前状态未知。";
            }
            DiagnosticLog.Record("lighting.detected",new{kind=keyboard.Kind,caps,modelHint,support2=hardware.State?.Support2,connected=hardware.Active});
            if (keyboard.Kind == "SingleZone"&&!hardware.SupportsDeclaredRgb)
            {
                try
                {
                var light = await Task.Run(() => hardware.ProbeLighting());
                lightingReadFresh=true;
                rgbLevel = (light.Backlight & 2) != 0 ? 0 : Math.Clamp(light.Backlight >> 5, 0, 4);
                rgbMode = rgbLevel == 0 ? "关闭背光" : (light.Trigger & Vault.RainbowMask) != 0 ? "彩虹循环" : "静态单色";
                // Preserve the actual RGB when changing brightness or effects. A
                // nearest named preset is a label, never a replacement readback.
                uint Channel(int value)=>(uint)Math.Clamp((int)Math.Round(value*255d/50),0,255);
                customRgb=Channel(light.Red)<<16|Channel(light.Green)<<8|Channel(light.Blue);
                rgbColor = colorPresets.MinBy(x => Math.Pow((x.Hex>>16)-light.Red*255d/50,2)+Math.Pow(((x.Hex>>8)&255)-light.Green*255d/50,2)+Math.Pow((x.Hex&255)-light.Blue*255d/50,2)).Name;
                }
                catch(Exception error){lightingProbeError=error.Message;DiagnosticLog.Failure("lighting.initial_read",error,new{keyboard.Kind});}
            }
            if (EcReader.IsAllowed(identity))
            {
                gpuState=await Task.Run(upstream.ReadGpuMode);
                gpuReadFresh=gpuState.Known;
                if(!UsesDeclaredGpuMux&&!gpuState.Known)gpuState=GpuModeCache.Read()??gpuState;
                if(gpuReadFresh&&!UsesDeclaredGpuMux)
                {
                    gpuBoot=GpuBootStateStore.Open(gpuState.Mode,requestedGpu,gpuSavedAt);
                    if(!uiReviewMode)GpuBootStateStore.Save(gpuBoot);
                    requestedGpu=GpuRestartPending?gpuState.Mode:0;
                    if(!GpuRestartPending)gpuSavedAt=0;
                    await SavePreferences();
                    var probe=Task.Run(GpuRuntimeCapabilities.Probe);
                    gpuRuntime=await Task.WhenAny(probe,Task.Delay(5000))==probe?await probe:new(null,null,null,null,"ACPI capability query timed out");
                    try
                    {
                        Directory.CreateDirectory(AppData.DiagnosticsPath);
                        await File.WriteAllTextAsync(Path.Combine(AppData.DiagnosticsPath,"gpu-runtime-probe.json"),System.Text.Json.JsonSerializer.Serialize(new{at=DateTimeOffset.Now,administratorToken=GpuModeControl.IsAdministrator(),gpuRuntime,gpuBoot,firmware=gpuState}));
                    }
                    catch(IOException){}catch(UnauthorizedAccessException){}
                }
                gpuModeLabel=gpuReadFresh?OriginalGpuName(gpuState.Mode):"待读取";
            }
            if (GpuRestartPending) gpuModeLabel = "待重启";
        }
        catch (Exception error) { DiagnosticLog.Failure("hardware.panel_initialization",error,new{keyboard.Kind,lightingReadFresh});actions.Add("Core 识别：" + error.Message); }
        await ReadOverviewPowerTargets();
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
        if(closed||gpuChanging||(target!=0&&(!hardware.Active||!gpuReadFresh))||upstream==null)return;
        if(UsesDeclaredGpuMux)
        {
            var core = upstream;
            if(target == 0)
            {
                gpuState = await Task.Run(core.ReadGpuMode);
                gpuReadFresh = gpuState.Known;
                UpdateGpuControls();
                return;
            }

            if(gpuState?.Supports(target) == true)
                await RunHardware(() => hardware.ApplyDeclaredGpuMux(target, core), "Core 已发送显卡模式命令。请刷新读取状态。");
            return;
        }
        if(target!=0&&gpuState?.Mode==target&&(target==3||GpuRuntimeMode==target||gpuRuntime?.HybridIgpuSupported!=true||gpuBoot?.InitialMode==3))return;
        await operationQueue.WaitAsync();
        if(closed){operationQueue.Release();return;}
        bool runtimeChanged=false;gpuChanging=true;UpdateGpuControls();
        try
        {
            if(ecTask!=null)try{await ecTask;}catch{}
            if(temperatureTask!=null)try{await temperatureTask;}catch{}
            // Persist the immutable baseline BEFORE staging, including when the
            // application is later closed and reopened before Windows restarts.
            if(gpuState?.Known==true)
            {
                gpuBoot??=GpuBootStateStore.Open(gpuState.Mode,requestedGpu,gpuSavedAt);
                GpuBootStateStore.Save(gpuBoot);
            }
            var reply=await GpuModeControl.Request(target,gpuState?.Known==true?gpuState.Mode:0);
            if(closed)return;
            if(reply.Success&&target is 1 or 2&&gpuBoot?.InitialMode is 1 or 2&&gpuRuntime is {HybridIgpuSupported:true,IgpuActive:bool expected})
            {runtimeChanged=expected!=(target==1);gpuRuntime=await Task.Run(()=>hardware.SwitchHybridRuntime(expected,target==1));}
            else if(target==0)gpuRuntime=await Task.Run(GpuRuntimeCapabilities.Probe);
            if(reply.State?.Known==true)
            {
                gpuReadFresh=true;
                gpuState=reply.State;
                gpuBoot=(gpuBoot??GpuBootStateStore.Open(gpuState.Mode,requestedGpu,gpuSavedAt)).Observe(gpuState.Mode);
                GpuBootStateStore.Save(gpuBoot);
                requestedGpu=GpuRestartPending?gpuState.Mode:0;
                gpuSavedAt=GpuRestartPending?DateTimeOffset.UtcNow.ToUnixTimeSeconds():0;
                await SavePreferences();
            }
            if(!reply.Success){await GpuMessage("显卡设置未完成",reply.Error??"请重新读取显卡设置。");return;}
            if(reply.Changed||runtimeChanged)
            {
                ConfirmedActionOsd(OriginalGpuName(target)+(GpuRestartPending?" · 待重启":" · 已生效，无需重启"),target==1?"gpu-integrated":target==2?"gpu-hybrid":"gpu-discrete");
                await GpuMessage(GpuRestartPending?"显卡模式已保存":"显卡设置已确认",GpuRestartPending?$"已选择{OriginalGpuName(target)}，重启后生效。准备好后请自行重启电脑。":"已确认当前显卡模式，无需为此次切换重启。");
            }
            else if(target!=0)await GpuMessage("无需更改",GpuRestartPending?"此模式已经保存，重启后生效。":"固件已设置为此模式。");
        }
        catch(System.ComponentModel.Win32Exception error) when(error.NativeErrorCode==1223){if(!closed)await GpuMessage("已取消","没有执行显卡操作。");}
        catch(Exception error){gpuReadFresh=false;gpuRuntime=null;actions.Add(error.ToString());if(!closed)await GpuMessage("显卡结果未确认","请点击显卡模式标题重新读取设置。");}
        finally{gpuChanging=false;lastEc=DateTimeOffset.MinValue;operationQueue.Release();if(!closed)UpdateGpuControls();}
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
    private void SamplePanelTemperatures()
    {
        if (temperatureTask is { IsCompleted:true })
        {
            temperature = temperatureTask.IsCompletedSuccessfully ? temperatureTask.Result : new(null,null); temperatureTask = null;
        }
        if (!gpuChanging && temperatureTask == null && DateTimeOffset.Now-lastTemperature >= TimeSpan.FromSeconds(6))
        {
            lastTemperature=DateTimeOffset.Now;
            bool sampleGpu = latest?.OnAc == true && ec.GpuFan is > 0;
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




