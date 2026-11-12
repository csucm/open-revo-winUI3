using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core.Hal;
using OpenRevo.Core;
using OpenRevo.ResidentProtocol;
using Windows.Globalization.NumberFormatting;

namespace OpenRevo.Native;

public sealed partial class MainWindow
{
    private Action? captureResidentDeclaredDraft;

    private FrameworkElement PaintDeclaredCpuTuning(DeclaredCpuTuningLimits limits)
    {
        var pl1=new NumberBox{Header="持续功耗 SPL (W)",Minimum=limits.Pl1Min,Maximum=limits.Pl1Max};
        var pl2=new NumberBox{Header="短时功耗 sPPT (W)",Minimum=limits.Pl2Min,Maximum=limits.Pl2Max};
        var temperature=new NumberBox{Header="温度墙 (°C)，留空可跳过",Minimum=70,Maximum=100};
        var local=residentClient==null?hardware.SavedDeclaredTuning:null;
        var saved=residentClient!=null?residentDeclaredDraft??residentTuning?.Declared?.Saved
            :local==null?null:new PanelDeclaredProfile(local.Pl1,local.Pl2,local.Temperature,local.CustomFans,
                EncodeCurve(local.CpuCurve is {Length:8} localCpu?localCpu:TuningProfile.DefaultCurve()),
                EncodeCurve(local.GpuCurve is {Length:8} localGpu?localGpu:TuningProfile.DefaultCurve()),local.RestoreOnAc);
        if(saved!=null)
        {
            pl1.Value=saved.Pl1 is int sustained?sustained:double.NaN;
            pl2.Value=saved.Pl2 is int boost?boost:double.NaN;
            if(saved.Temperature!=0)temperature.Value=saved.Temperature;
        }
        FanPoint[] cpuCurve=PanelFanPointShape(saved?.CpuCurve)?DecodeCurve(saved!.CpuCurve):TuningProfile.DefaultCurve();
        FanPoint[] gpuCurve=PanelFanPointShape(saved?.GpuCurve)?DecodeCurve(saved!.GpuCurve):TuningProfile.DefaultCurve();
        var customFans=new CheckBox{Content="应用自定义风扇曲线",IsChecked=saved?.CustomFans==true};
        var restore=new CheckBox{Content="接管、唤醒或恢复交流供电时应用已保存预设",IsChecked=saved?.RestoreOnAc==true};
        PanelDeclaredProfile Form()=>new(
            double.IsFinite(pl1.Value)?(int)pl1.Value:null,double.IsFinite(pl2.Value)?(int)pl2.Value:null,
            double.IsNaN(temperature.Value)?0:(int)temperature.Value,limits.FanCurve&&customFans.IsChecked==true,
            EncodeCurve(cpuCurve),EncodeCurve(gpuCurve),restore.IsChecked==true);
        void Changed(){if(residentClient!=null)TrackResidentDeclaredDraft(Form());}
        Func<bool>? canCommit=residentClient==null?null:()=>!residentDetaching&&!closed;
        var curves=Paint.Stack(8,Paint.Text("CPU 风扇曲线"),new PaintFanEditor(cpuCurve,next=>{cpuCurve=next;Changed();},canCommit),
            Paint.Text("GPU 风扇曲线"),new PaintFanEditor(gpuCurve,next=>{gpuCurve=next;Changed();},canCommit));
        curves.Visibility=customFans.IsChecked==true?Visibility.Visible:Visibility.Collapsed;
        customFans.Checked+=(_,_)=>{curves.Visibility=Visibility.Visible;Changed();};
        customFans.Unchecked+=(_,_)=>{curves.Visibility=Visibility.Collapsed;Changed();};
        if(residentClient!=null)
        {
            captureResidentDeclaredDraft=()=>TrackResidentDeclaredDraft(Form() with
            {
                Pl1=ReadDeclaredDraftNumber(pl1),
                Pl2=ReadDeclaredDraftNumber(pl2),
                Temperature=ReadDeclaredDraftNumber(temperature)??0
            });
            pl1.ValueChanged+=(_,_)=>Changed();
            pl2.ValueChanged+=(_,_)=>Changed();
            temperature.ValueChanged+=(_,_)=>Changed();
            restore.Checked+=(_,_)=>Changed();
            restore.Unchecked+=(_,_)=>Changed();
        }
        async Task Submit(bool save)
        {
            if(uiReviewMode||!save&&(!PanelHardwareActive||PanelTuningBusy))return;
            var draft=Form();
            if(draft.Pl1 is not int spl||draft.Pl2 is not int sppt||!draft.Valid||pl1.Value>pl2.Value||
                pl1.Value<limits.Pl1Min||pl1.Value>limits.Pl1Max||pl2.Value<limits.Pl2Min||pl2.Value>limits.Pl2Max||
                residentClient!=null&&(pl1.Value!=spl||pl2.Value!=sppt||!double.IsNaN(temperature.Value)&&temperature.Value!=draft.Temperature))
            {await ShowMessage("请检查功耗与温度","填写 SPL 和 sPPT，且 SPL 不得高于 sPPT。温度墙留空或填写 70–100 °C。");return;}
            if(residentClient!=null){await SubmitResidentDeclaredTuningAsync(draft,save);return;}
            if(CoreMachine.Current.Manifest is not string manifest)return;
            var preset=new DeclaredTuningPreset(manifest,spl,sppt,draft.Temperature,draft.CustomFans,cpuCurve,gpuCurve,draft.RestoreOnAc);
            if(!save){await ApplyDeclaredPreset(preset);return;}
            AppData.Update(p=>p with{DeclaredTuning=preset});
            await ShowMessage("预设已保存","可从自定义档位再次应用。");
        }
        var save=PB("保存预设",()=>Submit(true));
        var apply=PB("应用 CPU 调校",()=>Submit(false),true);
        paintBindings.Add(()=>
        {
            apply.IsEnabled=PanelHardwareActive&&!PanelTuningBusy&&!tuningLoading;
            save.IsEnabled=residentClient==null||!PanelTuningBusy&&!tuningLoading;
        });
        var content=Paint.Stack(12,Paint.Text("当前硬件调校参数未知。填写后可发送 Core 命令。",12,muted:true),
            pl1,pl2,temperature);
        if(limits.FanCurve){content.Children.Add(customFans);content.Children.Add(curves);}
        content.Children.Add(restore);
        content.Children.Add(Paint.Inline(8,save,apply));
        if(residentClient!=null)
            content.Children.Add(PB("重新读取",()=>PaintReadTuning()));
        return Paint.Card(content,16);
    }

    private static int? ReadDeclaredDraftNumber(NumberBox field)
    {
        // NumberBox evaluates its InputBox on Enter or loss of focus. Read the
        // current text without changing Value or triggering its event handlers.
        string text=DetailDescendants(field).OfType<TextBox>()
            .FirstOrDefault(input=>input.Name=="InputBox")?.Text??field.Text;
        if(string.IsNullOrWhiteSpace(text))return null;
        double? parsed=(field.NumberFormatter as INumberParser)?.ParseDouble(text.Trim());
        double value=parsed??double.NaN;
        if(!double.IsFinite(value)||value!=Math.Truncate(value)||value<field.Minimum||value>field.Maximum)
            value=field.Value;
        return double.IsFinite(value)&&value==Math.Truncate(value)&&value>=field.Minimum&&value<=field.Maximum&&
            value>=int.MinValue&&value<=int.MaxValue?(int)value:null;
    }

    private static bool PanelFanPointShape(PanelFanPoint[]? curve) => curve is {Length:8} && curve.All(p=>p!=null);
    private Task ApplyDeclaredPreset(DeclaredTuningPreset preset) =>
        RunPerformance(()=>performance.ApplyDeclaredTuningAsync(preset),"Core 已发送 CPU 调校命令，未回读确认。");

    private async Task RestoreDeclaredTuningOnAc()
    {
        if(uiReviewMode||closed||exitRequested||!performance.ShouldRestoreDeclaredOnAc(latest?.OnAc))return;
        if(hardwareBusy||quickBusy||gpuChanging||desktopServiceBusy||batteryGuardBusy||sleepOfficeBusy||settingsDataBusy||settingsPowerBusy||takeoverChanging)return;
        await RunPerformance(performance.RestoreDeclaredOnAcAsync,"Core 已发送 CPU 调校命令，未回读确认。");
    }
}
