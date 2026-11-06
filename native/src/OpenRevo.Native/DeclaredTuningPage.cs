using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core.Hal;
using OpenRevo.Core;

namespace OpenRevo.Native;

public sealed partial class MainWindow
{
    private FrameworkElement PaintDeclaredCpuTuning(DeclaredCpuTuningLimits limits)
    {
        var pl1=new NumberBox{Header="持续功耗 SPL (W)",Minimum=limits.Pl1Min,Maximum=limits.Pl1Max};
        var pl2=new NumberBox{Header="短时功耗 sPPT (W)",Minimum=limits.Pl2Min,Maximum=limits.Pl2Max};
        var temperature=new NumberBox{Header="温度墙 (°C)，留空可跳过",Minimum=70,Maximum=100};
        var saved=hardware.SavedDeclaredTuning;
        if(saved!=null)
        {
            pl1.Value=saved.Pl1;pl2.Value=saved.Pl2;
            if(saved.Temperature!=0)temperature.Value=saved.Temperature;
        }
        FanPoint[] cpuCurve=saved?.CpuCurve is {Length:8} cpu?cpu:TuningProfile.DefaultCurve();
        FanPoint[] gpuCurve=saved?.GpuCurve is {Length:8} gpu?gpu:TuningProfile.DefaultCurve();
        var customFans=new CheckBox{Content="应用自定义风扇曲线",IsChecked=saved?.CustomFans==true};
        var curves=Paint.Stack(8,Paint.Text("CPU 风扇曲线"),new PaintFanEditor(cpuCurve,next=>cpuCurve=next),
            Paint.Text("GPU 风扇曲线"),new PaintFanEditor(gpuCurve,next=>gpuCurve=next));
        curves.Visibility=customFans.IsChecked==true?Visibility.Visible:Visibility.Collapsed;
        customFans.Checked+=(_,_)=>curves.Visibility=Visibility.Visible;
        customFans.Unchecked+=(_,_)=>curves.Visibility=Visibility.Collapsed;
        var restore=new CheckBox{Content="接管、唤醒或恢复交流供电时应用已保存预设",IsChecked=saved?.RestoreOnAc==true};
        DeclaredTuningPreset? Draft()
        {
            if(!double.IsFinite(pl1.Value)||!double.IsFinite(pl2.Value)||pl1.Value>pl2.Value||
                pl1.Value<limits.Pl1Min||pl1.Value>limits.Pl1Max||pl2.Value<limits.Pl2Min||pl2.Value>limits.Pl2Max||
                CoreMachine.Current.Manifest is not string manifest)return null;
            return new(manifest,(int)pl1.Value,(int)pl2.Value,double.IsNaN(temperature.Value)?0:(int)temperature.Value,
                limits.FanCurve&&customFans.IsChecked==true,cpuCurve,gpuCurve,restore.IsChecked==true);
        }
        var save=PB("保存预设",async ()=>
        {
            if(uiReviewMode)return;
            if(Draft() is not {} preset){await ShowMessage("请检查功耗","填写 SPL 和 sPPT，且 SPL 不得高于 sPPT。");return;}
            AppData.Update(p=>p with{DeclaredTuning=preset});
            await ShowMessage("预设已保存","可从自定义档位再次应用。");
        });
        var apply=PB("应用 CPU 调校",async ()=>
        {
            if(uiReviewMode||!hardware.Active||hardwareBusy)return;
            if(Draft() is not {} preset){await ShowMessage("请检查功耗","填写 SPL 和 sPPT，且 SPL 不得高于 sPPT。");return;}
            await ApplyDeclaredPreset(preset);
        },true);
        paintBindings.Add(()=>apply.IsEnabled=hardware.Active&&!hardwareBusy);
        var content=Paint.Stack(12,Paint.Text("当前硬件调校参数未知。填写后可发送 Core 命令。",12,muted:true),
            pl1,pl2,temperature);
        if(limits.FanCurve){content.Children.Add(customFans);content.Children.Add(curves);}
        content.Children.Add(restore);
        content.Children.Add(Paint.Inline(8,save,apply));
        return Paint.Card(content,16);
    }
    private Task ApplyDeclaredPreset(DeclaredTuningPreset preset) =>
        RunHardware(()=>hardware.ApplyDeclaredCpuTuning(preset),"Core 已发送 CPU 调校命令，未回读确认。");

    private bool? declaredRestoreAc;
    private bool declaredPowerSuspended;

    private async Task RestoreDeclaredTuningOnAc()
    {
        if(!hardware.Active){declaredRestoreAc=null;return;}
        if(hardware.DeclaredCpuTuning==null||uiReviewMode||closed||exitRequested||declaredPowerSuspended||latest?.OnAc is not bool ac)return;
        if(!ac){declaredRestoreAc=false;return;}
        if(declaredRestoreAc==true||hardwareBusy||quickBusy||gpuChanging||desktopServiceBusy||batteryGuardBusy||sleepOfficeBusy||settingsDataBusy||settingsPowerBusy||takeoverChanging)return;
        declaredRestoreAc=true;
        if(hardware.SavedDeclaredTuning is {RestoreOnAc:true} preset)await ApplyDeclaredPreset(preset);
    }
}
