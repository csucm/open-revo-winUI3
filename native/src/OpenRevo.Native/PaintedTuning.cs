using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core;
using OpenRevo.Core.Hal;
using OpenRevo.ResidentProtocol;
using System.Text.Json;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private TuningRegisters? rejectedTuningRegisters;
    private string[] readOnlyTuningPresetNames=[];

    private static object FactoryTuningReadback(TuningRegisters r)=>new{
        r.OfficePl1,r.OfficePl2,r.OfficePl4,r.BalancePl1,r.BalancePl2,r.BalancePl4,r.TurboPl1,r.TurboPl2,r.TurboPl4};

    private void LoadPaintedPresetDraft(TuningPreset preset)
    {
        var source=preset.Profile.Copy();
        tuningDraft=FitPaintCpuDraft(source);
        tuningStatus=tuningDraft==source?"方案已载入草稿，尚未应用。":"已按当前功耗上限调整草稿，尚未保存或应用。";
    }

    private async Task PaintAdjustCpuCeiling(CpuPowerLimit limit)
    {
        if(uiReviewMode||tuningLoading||tuningApplying||!PaintClassicCpuTuning||tuningDraft==null)return;
        var state=tuningBaseline;
        var remote=residentTuning;
        int min,current,max,defaultValue;
        if(residentClient!=null)
        {
            if(remote?.Classic?.Cpu is not { } cpu)return;
            var ceiling=limit switch{CpuPowerLimit.Pl1=>cpu.Pl1,CpuPowerLimit.Pl2=>cpu.Pl2,_=>cpu.Pl4};
            (min,current,max,defaultValue)=(ceiling.Minimum,ceiling.Value,ceiling.Maximum,ceiling.Default);
        }
        else
        {
            (min,current,max)=state!.CpuCeilingRange(limit);
            defaultValue=(state with{CpuCeilings=null}).CpuCeilingRange(limit).Value;
        }
        var input=new PaintTextEntry(current.ToString());
        var content=Paint.Stack(8,Paint.Text($"可设 {min}–{max} W，默认 {defaultValue} W。",13),input);
        if(!await PaintDialog($"调整 {limit} 上限",content,"保存","取消"))return;
        if(closed||residentDetaching||tuningLoading||tuningApplying||!ReferenceEquals(tuningBaseline,state)||tuningDraft==null)return;
        if(!int.TryParse(input.Text,out int value)||value<min||value>max)
        {
            await Task.Yield();
            await ShowMessage("上限无效",$"请输入 {min}–{max} W 范围内的整数。");
            return;
        }
        if(remote!=null){await SaveResidentCpuCeilingAsync(remote,limit,value);return;}
        var previous=state!.CpuCeilings??new CpuPowerCeilings();
        var ceilings=limit switch
        {
            CpuPowerLimit.Pl1=>previous with{Pl1=value},
            CpuPowerLimit.Pl2=>previous with{Pl2=value},
            _=>previous with{Pl4=value}
        };
        AppData.Update(p=>p with{CpuPowerCeilings=ceilings});
        var next=state with{CpuCeilings=ceilings};
        tuningBaseline=next;
        tuningDraft=next.FitCpuDraft(tuningDraft);
        RememberPresetDraft();
        tuningStatus="已保存软件上限，尚未应用硬件。";
        RefreshPaintedState();
    }

    private async Task PaintReadTuning(bool automatic=false)
    {
        if(closed||tuningLoading||tuningApplying||uiReviewMode)return;
        if(residentClient!=null){await ReadResidentTuningAsync(automatic);return;}
        tuningLoading=true;
        string stage="panel";
        object? factory=null;
        using var trace=DiagnosticLog.Begin("tuning.read",new{automatic});
        try
        {
            if(automatic&&panelShapeChanging)await panelShapeCompletion;
            if(closed||mini||!customTuningOpen)return;
            tuningStatus="正在读取当前调校参数…";
            RefreshPaintedState();
            stage="hardware";
            var core=upstream;
            if(core==null||!hardware.Active)throw new IOException("请先连接设备，再读取调校参数。");
            var state=await Task.Run(()=>hardware.ProbeTuning(core));
            factory=state.CpuTuning?FactoryTuningReadback(state.Registers):null;
            rejectedTuningRegisters=null;
            stage="draft";
            var draft=hardware.TuningDraft(state);
            tuningBaseline=state;tuningDraft=draft;
            stage="presets";
            var originals=await Task.Run(()=>core.Read(18));
            InitializeTuningPresets(originals,state);
            if(automatic&&SelectedTuningPreset is {} selected)
            {
                LoadPaintedPresetDraft(selected);
            }
            else
            {
                tuningPresetId=null;
                tuningStatus="已读取当前硬件状态，编辑仅修改草稿。";
            }
            trace.Complete(new{factory,mode=state.Performance.Mode,state.CpuTuning,
                current=state.CpuTuning?new{state.Registers.Pl1,state.Registers.Pl2,state.Registers.Pl4}:null,
                limits=state.CpuTuning?new{state.Pl1Max,state.Pl2Max,state.Pl4Max}:null});
        }
        catch(TuningFactoryLimitsException error)
        {
            var r=rejectedTuningRegisters=error.Registers;
            tuningStatus="工厂功耗边界未通过校验。可查看出厂读数和内置方案名称。";
            trace.Fail(error,new{stage,connected=hardware.Active,factory=FactoryTuningReadback(r)});
            if(readOnlyTuningPresetNames.Length==0&&upstream is {} core&&!closed)
            {
                try
                {
                    var originals=await Task.Run(()=>core.Read(18));
                    readOnlyTuningPresetNames=originals.EnumerateArray()
                        .Select(p=>p.GetProperty("name").GetString()).OfType<string>().ToArray();
                }
                catch(Exception presetError){DiagnosticLog.Failure("tuning.preset_catalog.failed",presetError);}
            }
        }
        catch(Exception error)
        {
            tuningStatus="读取失败，可点击“重新读取”。"+error.Message;
            trace.Fail(error,new{stage,connected=hardware.Active,factory});
        }
        finally
        {
            tuningLoading=false;
            if(!closed&&!mini&&customTuningOpen)Render();
        }
    }
    private FrameworkElement PaintUnverifiedTuning(TuningRegisters r) => PaintUnverifiedTuning(
        new(r.OfficePl1,r.OfficePl2,r.OfficePl4),new(r.BalancePl1,r.BalancePl2,r.BalancePl4),
        new(r.TurboPl1,r.TurboPl2,r.TurboPl4),readOnlyTuningPresetNames);

    private FrameworkElement PaintUnverifiedTuning(PanelFactoryPower office,PanelFactoryPower balance,
        PanelFactoryPower turbo,string[] presetNames)
    {
        var retry=PB("重新读取",()=>PaintReadTuning());
        paintBindings.Add(()=>retry.IsEnabled=!tuningLoading&&!tuningApplying);
        var status=PT(()=>tuningStatus,12,muted:true);
        var rows=Paint.Stack(10,Paint.Between(status,retry),
            Paint.Text("出厂功耗寄存器原值 · PL1 / PL2 / PL4",14,true),
            Paint.Text($"办公  {office.Pl1} / {office.Pl2} / {office.Pl4}"),
            Paint.Text($"均衡  {balance.Pl1} / {balance.Pl2} / {balance.Pl4}"),
            Paint.Text($"狂暴  {turbo.Pl1} / {turbo.Pl2} / {turbo.Pl4}"));
        if(presetNames.Length>0)
            rows.Children.Add(Paint.Text("内置方案（只读）  "+string.Join(" · ",presetNames),12,muted:true));
        return PaintSection("方案与出厂读数","tuning",rows);
    }

    private async Task PaintApplyTuning()
    {
        if(residentClient!=null){await ApplyResidentClassicTuningAsync();return;}
        if(uiReviewMode||tuningLoading||tuningApplying||rejectedTuningRegisters!=null||tuningDraft==null||tuningBaseline==null||upstream==null||!hardware.Active)return;
        var state=tuningBaseline;
        var draft=tuningDraft.Copy();
        tuningApplying=true;tuningStatus="正在应用并核验…";RefreshPaintedState();
        try
        {
            var result=await RunPerformance(()=>performance.ApplyTuningAsync(state,draft),"极客调校已应用并回读确认。");
            if(result.Tuning is {After:var after})
            {
                tuningBaseline=after;tuningDraft=hardware.TuningDraft(after) with{Offsets=draft.Offsets};
                RememberPresetDraft();tuningStatus="极客调校已应用并回读确认。";
            }
            else tuningStatus="应用未完成，请重新读取。";
        }
        finally{tuningApplying=false;if(!closed)Render();}
    }
    private FrameworkElement PaintFanCurves()
    {
        if(!PaintClassicTuningReady||tuningDraft is not{} p)return Paint.Text("风扇曲线待读取。",12,muted:true);
        Func<bool>? canCommit=residentClient==null?null:()=>!residentDetaching&&!closed;
        var cpu=Paint.Card(Paint.Stack(8,Paint.Text("CPU 风扇曲线"),new PaintFanEditor(p.CpuCurve,next=>{tuningDraft=tuningDraft! with{CpuCurve=next};PaintDraftChanged();RefreshPaintedState();},canCommit)),14,12,true);
        var gpu=Paint.Card(Paint.Stack(8,Paint.Text("GPU 风扇曲线"),new PaintFanEditor(p.GpuCurve,next=>{tuningDraft=tuningDraft! with{GpuCurve=next};PaintDraftChanged();RefreshPaintedState();},canCommit)),14,12,true);
        var grid=new DesktopTiles{PreferredColumns=2,MinimumItemWidth=0,ViewportBreakpoint=960,ColumnSpacing=16,RowSpacing=16};grid.Children.Add(cpu);grid.Children.Add(gpu);var host=new ContentControl{Content=grid,HorizontalContentAlignment=HorizontalAlignment.Stretch};paintBindings.Add(()=>host.IsEnabled=!tuningLoading&&!tuningApplying&&(residentClient==null||!PanelTuningBusy));return host;
    }
    private FrameworkElement PaintTools(string route)
    {
        var tools=new SuiteWrapPanel();if(route=="color")
        {
            tools.Children.Add(PB("导入",async()=>{if(!uiReviewMode)await PaintImportColor();}));tools.Children.Add(PB("导出",async()=>{if(colorDraft!=null&&!uiReviewMode)await SaveJsonFile("色彩方案.json",colorDraft.Export());}));
            tools.Children.Add(PB("重置",async()=>
            {
                if(uiReviewMode||colorDraft==null)return;
                if(residentClient!=null){await ResetResidentColorAsync();return;}
                var original=serviceColor?.FactoryPresets.FirstOrDefault(p=>p.Id==colorDraft.Id);
                if(original!=null){colorDraft=original.Copy();await SubmitColor();Render();}
            }));return tools;
        }
        foreach(var(label,command)in new[]{("新建","new"),("保存","save"),("恢复内置","restore"),("重命名","rename"),("导入","import"),("导出","export"),("打开目录","folder"),("删除","delete")})
        {string name=command;var button=PB(label,()=>PaintPresetAction(name));paintBindings.Add(()=>button.IsEnabled=PaintClassicTuningReady&&tuningDraft!=null&&!tuningLoading&&!tuningApplying&&(residentClient==null||!PanelTuningBusy)&&(name!="new"||PaintCanCreateTuningPreset)&&(name!="restore"||SelectedTuningPreset?.BuiltIn==true)&&(name!="rename"||SelectedTuningPreset!=null)&&(name!="delete"||SelectedTuningPreset?.BuiltIn==false));tools.Children.Add(button);}return tools;
    }
    private async Task PaintPresetAction(string command)
    {
        if(residentClient!=null){await RunResidentPresetActionAsync(command);return;}
        if(tuningDraft==null||tuningBaseline is not{} state)return;
        TuningPreset Draft()=>SelectedTuningPreset is{} selected?selected with{Profile=tuningDraft.Copy()}:new("user_"+Guid.NewGuid().ToString("N"),"新方案","star",false,tuningDraft.Copy(),[]);
        if(uiReviewMode)return;
        switch(command)
        {
            case "new":
                string? name=await PaintPresetName("新建手动方案","自定义方案");
                if(name==null)return;
                var source=CreateManualTuningSource(state);
                var createdPreset=tuningPresetStore.Duplicate(source,name,state);
                RememberPresetDraft();
                tuningPresets.Add(createdPreset);
                tuningPresetId=createdPreset.Id;
                tuningDraft=createdPreset.Profile.Copy();
                tuningStatus=state.CpuTuning?"已按本机出厂均衡参数新建手动方案，尚未应用。":"已新建手动方案，应用时保留本机 CPU 参数。";
                break;
            case "save":var p=Draft();bool created=SelectedTuningPreset==null;if(created){string? n=await PaintPresetName("保存方案",p.Name);if(n==null)return;p=p with{Name=n};}tuningPresetStore.Save(p,state);if(created){tuningPresets.Add(p);tuningPresetId=p.Id;}RememberPresetDraft();tuningStatus="方案已保存，尚未应用。";break;
            case "restore":var original=tuningBuiltinDefaults.First(p=>p.Id==tuningPresetId);original=original with{Profile=state.FitCpuDraft(original.Profile).Copy()};tuningPresetStore.Save(original,state);tuningDraft=original.Profile.Copy();tuningPresets[tuningPresets.FindIndex(p=>p.Id==original.Id)]=original;tuningStatus="内置方案已恢复，尚未应用。";break;
            case "rename":var selected=SelectedTuningPreset!;string? renamed=await PaintPresetName("重命名方案",selected.Name);if(renamed==null)return;selected=selected with{Name=renamed};tuningPresetStore.Save(selected,state);tuningPresets[tuningPresets.FindIndex(p=>p.Id==selected.Id)]=selected;break;
            case "import":var open=new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id);open.FileTypeFilter.Add(".json");var file=await open.PickSingleFileAsync();if(file==null)return;if(new FileInfo(file.Path).Length>1024*1024)throw new IOException("方案文件不能超过1MiB。");var imported=tuningPresetStore.Import(await File.ReadAllTextAsync(file.Path),state);RememberPresetDraft();tuningPresets.Add(imported);tuningPresetId=imported.Id;tuningDraft=imported.Profile.Copy();tuningStatus="方案已导入，尚未应用。";break;
            case "export":var export=Draft();HardwareSession.ValidateTuningDraft(state,export.Profile);await SaveJsonFile(export.Name+".json",TuningPresetStore.Export(export));break;
            case "folder":OpenLocalFolder(tuningPresetStore.DirectoryPath);return;
            case "delete":var deleted=SelectedTuningPreset!;if(!await Confirm("删除“"+deleted.Name+"”？","方案文件会移到trash目录，当前硬件参数保持。"))return;tuningPresetStore.Delete(deleted);tuningPresets.RemoveAll(p=>p.Id==deleted.Id);tuningPresetId=null;break;
        }
        AppData.Update(p=>p with{SelectedTuningPreset=tuningPresetId});Render();
    }
    private async Task PaintImportColor()
    {
        var open=new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id);open.FileTypeFilter.Add(".json");var file=await open.PickSingleFileAsync();if(file==null)return;if(new FileInfo(file.Path).Length>1024*1024)throw new IOException("方案文件不能超过1MiB。");if(colorDraft==null)throw new IOException("请先读取色彩预设。");
        colorDraft.ImportParameters(await File.ReadAllTextAsync(file.Path));await SubmitColor();Render();
    }
}
