using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core;
using OpenRevo.Core.Hal;
using System.Text.Json;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private TuningSnapshot? tuningBaseline;
    private TuningProfile? tuningDraft;
    private bool tuningLoading,tuningApplying;
    private StackPanel? tuningPanel;
    private readonly List<NumberBox> tuningNumbers=[];
    private string tuningStatus="读取当前参数后开始编辑。";
    private TextBlock? tuningStatusText;
    private string tuningReturnPage="display";
    private bool tuningReturnMini;
    private string TuningReturnLabel=>tuningReturnMini?"Mini 面板":tuningReturnPage=="overview"?"状态总览":"显卡与屏幕";
    private void OpenTuning()
    {
        if(!customTuningOpen){tuningReturnPage=currentPage;tuningReturnMini=mini;}
        currentPage="display";customTuningOpen=true;colorTuningOpen=false;
        if(mini)ToggleMini();else Render();
        tuningPanel?.StartBringIntoView();
    }
    private FrameworkElement TuningEditor()
    {
        var host=tuningPanel=new StackPanel{Spacing=12};
        void Fill()=>FillTuningEditor(host);
        host.Children.Add(Ui.Text(tuningStatus,13,muted:true));
        if(tuningBaseline!=null&&tuningDraft!=null)Fill();
        else if(!tuningLoading)_=ReadTuningEditor(host);
        return host;
    }
    private async Task ReadTuningEditor(StackPanel host)
    {
        if(tuningLoading||tuningApplying||closed)return;
        tuningLoading=true;
        try
        {
            if(upstream==null||!hardware.Active)throw new InvalidOperationException("请先连接设备，再读取调校参数。");
            var state=await Task.Run(()=>hardware.ProbeTuning(upstream));
            if(closed)return;
            tuningBaseline=state;tuningDraft=hardware.TuningDraft(state);
            try{var originals=await Task.Run(()=>upstream.Read(18));InitializeTuningPresets(originals,state);}catch(Exception error){actions.Add("方案读取："+error.Message);}
            tuningStatus="已读取当前硬件状态。编辑仅修改草稿，点击应用后生效。";
        }
        catch(Exception error){tuningStatus=error.Message;actions.Add("调校读取："+error.Message);}
        finally{tuningLoading=false;if(!closed&&tuningPanel is {} current)FillTuningEditor(current);}
    }
    private void FillTuningEditor(StackPanel host)
    {
        host.Children.Clear();tuningNumbers.Clear();
        var status=tuningStatusText=Ui.Text(tuningStatus,12,muted:true);
        var read=Ui.Button("重新读取",async(_,_)=>await ReadTuningEditor(host));read.IsEnabled=!tuningApplying&&!tuningLoading;
        ToolTipService.SetToolTip(read,"重新读取硬件，替换当前草稿；不会应用方案");
        if(tuningBaseline is not{} state||tuningDraft==null){host.Children.Add(status);host.Children.Add(read);return;}
        var presetToolbar=TuningPresetToolbar(host,state);host.Children.Add(presetToolbar);
        if(SelectedTuningPreset is{} selected&&selected.Adjustments.Length>0)host.Children.Add(Ui.Text("本机适配："+string.Join("；",selected.Adjustments),11,muted:true));
        var editors=new TuningSection{Spacing=12};
        bool syncing=false;
        void Changed(){if(!syncing){RememberPresetDraft();tuningStatus="草稿已修改，尚未应用。";status.Text=tuningStatus;}}
        ContentControl Number(string label,int value,int min,int max,string unit,Action<int> set,int step=1)
        {
            var number=new NumberBox{Value=value,Minimum=min,Maximum=max,SmallChange=step,LargeChange=step*5,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Compact,Width=84,FontSize=14};tuningNumbers.Add(number);
            var slider=new Slider{Minimum=min,Maximum=max,StepFrequency=step,Value=value,MinHeight=26};bool linking=false;
            number.ValueChanged+=(_,e)=>{if(double.IsNaN(e.NewValue))return;if(!linking){linking=true;slider.Value=Math.Clamp(e.NewValue,min,max);linking=false;}if(!syncing){set((int)Math.Round(e.NewValue));Changed();}};
            slider.ValueChanged+=(_,_)=>{if(syncing||linking)return;linking=true;number.Value=slider.Value;linking=false;};
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(number,label+"（"+unit+"）");Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(slider,label+"滑块");
            var title=Ui.Text(label,14);title.TextWrapping=TextWrapping.NoWrap;
            var stack=SuiteVisuals.Stack(8,DesktopAlignedRow(title,SettingsControls(number,Ui.Text(unit,12,muted:true)),"tuning-number"),slider,Ui.Row(Ui.Text(min+" "+unit,12,muted:true),Ui.Text(max+" "+unit,12,muted:true)));
            return new ContentControl{Content=SuiteVisuals.Rule(stack,14),HorizontalContentAlignment=HorizontalAlignment.Stretch};
        }
        FrameworkElement Toggle(string label,bool value,Action<bool> set)
        {
            var toggle=ServiceToggle(label,value,(_,_)=>{});
            toggle.Toggled+=(_,_)=>{if(!syncing){set(toggle.IsOn);Changed();}};
            return DesktopAlignedRow(Ui.Text(label,14),toggle,"tuning-toggle");
        }
        var p=tuningDraft;
        var cpu=Ui.Stack(
            Number("持续功耗 PL1",p.Pl1,15,state.Pl1Max,"W",v=>tuningDraft=tuningDraft! with{Pl1=v}),
            Number("短时功耗 PL2",p.Pl2,15,state.Pl2Max,"W",v=>tuningDraft=tuningDraft! with{Pl2=v}),
            Number("瞬时功耗 PL4",p.Pl4,35,state.Pl4Max,"W",v=>tuningDraft=tuningDraft! with{Pl4=v}));cpu.Spacing=8;
        var temperature=Number("温度墙",p.TemperatureC,80,95,"°C",v=>tuningDraft=tuningDraft! with{TemperatureC=v});temperature.IsEnabled=p.TemperatureEnabled;
        cpu.Children.Add(Toggle("自定义温度墙",p.TemperatureEnabled,v=>{tuningDraft=tuningDraft! with{TemperatureEnabled=v};temperature.IsEnabled=v;}));cpu.Children.Add(temperature);
        cpu.Children.Add(Ui.Text("PL1 ≤ PL2；PL4 ≥ PL2 + 20W。",11,muted:true));
        FrameworkElement gpu;
        var caps=state.Gpu.Capabilities;
        if(caps.Supported)
        {
            var manual=new TuningSection{Spacing=8};
            var baseControl=Number("基础功耗",p.GpuBaseWatts,caps.FloorWatts,caps.BaseMaxWatts,"W",v=>tuningDraft=tuningDraft! with{GpuBaseWatts=v});baseControl.IsEnabled=caps.ConfigurableBase;manual.Children.Add(baseControl);
            var dbValue=Number("动态增强上限",p.GpuBoostWatts,5,caps.BoostMaxWatts,"W",v=>tuningDraft=tuningDraft! with{GpuBoostWatts=v});dbValue.IsEnabled=p.GpuBoostEnabled;
            manual.Children.Add(Toggle("Dynamic Boost",p.GpuBoostEnabled,v=>{tuningDraft=tuningDraft! with{GpuBoostEnabled=v};dbValue.IsEnabled=v;}));manual.Children.Add(dbValue);
            var clocks=new TuningSection{Spacing=8};
            var coreControl=Number("核心频率偏移",Math.Clamp(p.Offsets?.CoreMhz??0,0,caps.CoreOffsetMax),0,caps.CoreOffsetMax,"MHz",v=>tuningDraft=tuningDraft! with{Offsets=(tuningDraft!.Offsets??new(0,0)) with{CoreMhz=v}});
            var memoryControl=Number("显存频率偏移",Math.Clamp(p.Offsets?.MemoryMhz??0,0,caps.MemoryOffsetMax),0,caps.MemoryOffsetMax,"MHz",v=>tuningDraft=tuningDraft! with{Offsets=(tuningDraft!.Offsets??new(0,0)) with{MemoryMhz=v}});
            coreControl.IsEnabled=caps.CoreOffsetMax>0;memoryControl.IsEnabled=caps.MemoryOffsetMax>0;clocks.Children.Add(coreControl);clocks.Children.Add(memoryControl);clocks.IsEnabled=p.GpuSuperPerformance&&p.Offsets!=null;manual.IsEnabled=!p.GpuSuperPerformance;
            var super=Toggle("超级性能",p.GpuSuperPerformance,v=>
            {
                tuningDraft=tuningDraft! with{GpuSuperPerformance=v};manual.IsEnabled=!v;clocks.IsEnabled=v&&state.Gpu.Offsets!=null;
                if(!v&&tuningDraft.Offsets!=null){tuningDraft=tuningDraft with{Offsets=new(0,0)};syncing=true;foreach(var box in DescendantNumbers(coreControl).Concat(DescendantNumbers(memoryControl)))box.Value=0;syncing=false;}
            });
            gpu=Ui.Stack(Ui.Text(caps.Name,11,muted:true),super,manual,clocks,Ui.Text(p.Offsets==null?"驱动未提供频率偏移读数。":"超级性能使用硬件最高功耗额度，并允许频率编辑。",11,muted:true));((StackPanel)gpu).Spacing=8;
        }
        else gpu=Ui.Text("未读到可确认的显卡能力。本次应用保留显卡参数。",12,muted:true);
        var pair=TuningColumns(ReplicaCard("cpu","CPU 参数",cpu),ReplicaCard("gpu","GPU 参数",gpu));editors.Children.Add(pair);
        var curveGrid=TuningColumns(
            CurvePanel("CPU 风扇曲线",new FanCurveEditor(p.CpuCurve,v=>{tuningDraft=tuningDraft! with{CpuCurve=v};Changed();},tuningNumbers)),
            CurvePanel("GPU 风扇曲线",new FanCurveEditor(p.GpuCurve,v=>{tuningDraft=tuningDraft! with{GpuCurve=v};Changed();},tuningNumbers,true)));var curves=new ContentControl{Content=curveGrid,HorizontalContentAlignment=HorizontalAlignment.Stretch,IsEnabled=p.CustomFans};
        var step=Number("每级响应时间",p.FanStepMs,100,12700,"ms",v=>tuningDraft=tuningDraft! with{FanStepMs=v},100);step.IsEnabled=p.SmoothFans;
        var fanSwitch=Toggle("自定义风扇曲线",p.CustomFans,v=>{tuningDraft=tuningDraft! with{CustomFans=v};curves.IsEnabled=v;});
        var smooth=Toggle("平滑调速",p.SmoothFans,v=>{tuningDraft=tuningDraft! with{SmoothFans=v};step.IsEnabled=v;});
        var options=TuningColumns(smooth,step);
        var fans=Ui.Stack(curves,options,Ui.Text("温度递增、转速不下降；最迟95°C达到100%。关闭自定义曲线则使用工厂狂暴风扇表。",11,muted:true));fans.Spacing=8;
        editors.Children.Add(ReplicaCard("fan","风扇曲线与响应",fans,fanSwitch));
        var apply=Ui.Button("应用极客调校",async (_,_)=>
        {
            if(tuningApplying||upstream==null||tuningDraft==null)return;
            try
            {
                if(tuningNumbers.Any(n=>double.IsNaN(n.Value)||n.Value!=Math.Round(n.Value)))throw new ArgumentException("请填写完整的整数参数。");
                HardwareSession.ValidateTuning(state,tuningDraft);
                var requested=tuningDraft.Copy();tuningApplying=true;editors.IsEnabled=false;read.IsEnabled=false;presetToolbar.IsEnabled=false;
                        status.Text="正在应用并核验…";
                await RunHardware(()=>hardware.ApplyTuning(upstream,state,requested),"极客调校已应用并回读确认。");
                if(hardware.Active&&hardware.TuningState is {} after&&!ReferenceEquals(after,state))
                {tuningBaseline=after;tuningDraft=hardware.TuningDraft(after);RememberPresetDraft();tuningStatus="极客调校已应用并回读确认。";}
                else tuningStatus="应用未完成，请重新读取当前硬件状态。";
            }
            catch(Exception error){tuningStatus=error.Message;}
            finally{tuningApplying=false;if(!closed&&ReferenceEquals(host,tuningPanel))FillTuningEditor(host);}
        },true);
        apply.IsEnabled=hardware.Active&&!tuningApplying;
        host.Children.Add(editors);
        var dock=Ui.Card(DesktopAlignedRow(status,SettingsControls(read,apply)));dock.Padding=new Thickness(16,12,16,12);dock.CornerRadius=new CornerRadius(12);
        host.Children.Add(dock);
        editors.IsEnabled=presetToolbar.IsEnabled=!tuningApplying;
    }
    private static IEnumerable<NumberBox> DescendantNumbers(UIElement element)
    {
        if(element is NumberBox number)yield return number;
        int count=Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element);
        for(int i=0;i<count;i++)if(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(element,i) is UIElement child)foreach(var item in DescendantNumbers(child))yield return item;
    }
}
internal sealed class TuningSection:ContentControl
{
    private readonly StackPanel panel=new();
    public TuningSection(){Content=panel;HorizontalContentAlignment=HorizontalAlignment.Stretch;}
    public UIElementCollection Children=>panel.Children;
    public double Spacing{get=>panel.Spacing;set=>panel.Spacing=value;}
}
