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
    private GpuBootState? gpuBoot;
    private GpuRuntimeCapabilities? gpuRuntime;
    private bool GpuRestartPending => gpuBoot?.Pending==true;
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
            var caps = await Task.Run(() => upstream.Read(5));
            string? modelHint = null;
            var catalog = await Task.Run(() => upstream.Read(6));
            foreach (var model in catalog.EnumerateArray())
                if (model.GetProperty("model_id").GetString()?.Equals(identity.Board,StringComparison.OrdinalIgnoreCase)==true && model.TryGetProperty("keyboard_type",out var hint))
                    modelHint=hint.GetString();
            keyboard = KeyboardCapability.Detect(caps.GetProperty("per_key").GetBoolean(), caps.GetProperty("four_zone").GetBoolean(),
                hardware.State is { } state && (state.Support2 & Vault.SingleZoneSupportMask) != 0, modelHint);
            DiagnosticLog.Record("lighting.detected",new{kind=keyboard.Kind,caps,modelHint,support2=hardware.State?.Support2,connected=hardware.Active});
            if (keyboard.Kind == "SingleZone")
            {
                try
                {
                var light = await Task.Run(() => hardware.ProbeLighting());
                lightingReadFresh=true;
                rgbLevel = (light.Backlight & 2) != 0 ? 0 : Math.Clamp(light.Backlight >> 5, 0, 4);
                rgbMode = rgbLevel == 0 ? "关闭背光" : (light.Trigger & Vault.RainbowMask) != 0 ? "彩虹循环" : "静态单色";
                rgbColor = colorPresets.MinBy(x => Math.Pow((x.Hex>>16)-light.Red*255d/50,2)+Math.Pow(((x.Hex>>8)&255)-light.Green*255d/50,2)+Math.Pow((x.Hex&255)-light.Blue*255d/50,2)).Name;
                }
                catch(Exception error){lightingProbeError=error.Message;DiagnosticLog.Failure("lighting.initial_read",error,new{keyboard.Kind});}
            }
            if (EcReader.IsAllowed(identity))
            {
                gpuState=await Task.Run(upstream.ReadGpuMode);
                gpuReadFresh=gpuState.Known;
                if(!gpuState.Known)gpuState=GpuModeCache.Read()??gpuState;
                if(gpuReadFresh)
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
            var content=Ui.Stack(OriginalDeviceGraphic(definition.Icon,15),Ui.Text(definition.Title,12));content.Spacing=2;content.HorizontalAlignment=HorizontalAlignment.Center;
            button.Content=content;button.Padding=new Thickness(3);button.Height=44;
            button.IsEnabled=hardware.Active&&upstream!=null&&!gpuChanging&&gpuReadFresh&&gpuState?.Supports(value)==true;
            if(gpuState?.Mode==value)button.Style=(Style)Application.Current.Resources["SelectedChoiceStyle"];
            ToolTipService.SetToolTip(button,definition.Description+"\n保存下次启动设置；支持情况以硬件回读为准。");gpuButtons[value]=button;return button;
        }
        // Keep the existing mini order, columns and 44-DIP button height.
        return Columns(Mode(1),Mode(2),Mode(3));
    }

    private async Task RunGpuRequest(int target)
    {
        if(closed||gpuChanging||(target!=0&&(!hardware.Active||!gpuReadFresh))||upstream==null)return;
        await operationQueue.WaitAsync();
        if(closed){operationQueue.Release();return;}
        gpuChanging=true;UpdateGpuControls();
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
            if(reply.Changed)
            {
                await GpuMessage(GpuRestartPending?"显卡模式已保存":"已撤销显卡切换",GpuRestartPending?$"已选择{OriginalGpuName(target)}，重启后生效。准备好后请自行重启电脑。":"已恢复本次开机的显卡设置，无需为此次切换重启。");
            }
            else if(target!=0)await GpuMessage("无需更改",GpuRestartPending?"此模式已经保存，重启后生效。":"固件已设置为此模式。");
        }
        catch(System.ComponentModel.Win32Exception error) when(error.NativeErrorCode==1223){if(!closed)await GpuMessage("已取消","没有执行显卡操作。");}
        catch(Exception error){actions.Add(error.ToString());if(!closed)await GpuMessage("显卡结果未确认","请点击显卡模式标题重新读取设置。");}
        finally{gpuChanging=false;lastEc=DateTimeOffset.MinValue;operationQueue.Release();if(!closed)UpdateGpuControls();}
    }
    private Task GpuMessage(string title,string message)
    {
        if(Environment.GetCommandLineArgs().Contains("--ui-gpu-checks")){actions.Add(title+"："+message);return Task.CompletedTask;}
        return ShowMessage(title,message);
    }
    private void UpdateGpuControls()
    {
        gpuModeLabel=!gpuReadFresh?"待读取":GpuRestartPending?"待重启":gpuState?.Known==true?OriginalGpuName(gpuState.Mode):"待读取";
        Put("gpuTitle","显卡模式："+gpuModeLabel);
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
            temperatureTask=Task.Run(()=>temperatures.Read(sampleGpu));
        }
        string Temp(int? value) => value is int n ? $"{n}°C" : "—";
        string Fan(int? value) => value is int n ? $"fans: {n} RPM" : "fans: — RPM";
        Put("miniCpu",$"CPU {Temp(temperature.Cpu)} · {Fan(ec.CpuFan)}");
        Put("miniGpu",$"GPU {Temp(temperature.Gpu)} · {Fan(ec.GpuFan)}");
        Put("cpuTemperature",$"CPU 温区 {Temp(temperature.Cpu)}");
        Put("gpuTemperature",temperature.Gpu.HasValue ? Temp(temperature.Gpu) : "休眠或暂不可用");
    }
}
