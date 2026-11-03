using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core;
using OpenRevo.Core.Hal;
using System.Text.Json;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private async Task PaintReadTuning()
    {
        if(tuningLoading||tuningApplying||uiReviewMode)return;tuningLoading=true;
        try{if(upstream==null||!hardware.Active)throw new IOException("请先连接设备，再读取调校参数。");var state=await Task.Run(()=>hardware.ProbeTuning(upstream));tuningBaseline=state;tuningDraft=hardware.TuningDraft(state);var originals=await Task.Run(()=>upstream.Read(18));InitializeTuningPresets(originals,state);tuningStatus="已读取当前硬件状态，编辑仅修改草稿。";}
        catch(Exception error){tuningStatus=error.Message;}finally{tuningLoading=false;if(!closed&&PaintRoute=="tuning")Render();}
    }
    private async Task PaintApplyTuning()
    {
        if(uiReviewMode||tuningApplying||tuningDraft==null||tuningBaseline==null||upstream==null||!hardware.Active)return;
        var state=tuningBaseline;var requested=tuningDraft.Copy();HardwareSession.ValidateTuning(state,requested);tuningApplying=true;tuningStatus="正在应用并核验…";RefreshPaintedState();
        try{await RunHardware(()=>hardware.ApplyTuning(upstream,state,requested),"极客调校已应用并回读确认。");if(hardware.Active&&hardware.TuningState is{} after&&!ReferenceEquals(after,state)){tuningBaseline=after;tuningDraft=hardware.TuningDraft(after);RememberPresetDraft();tuningStatus="极客调校已应用并回读确认。";}else tuningStatus="应用未完成，请重新读取。";}
        finally{tuningApplying=false;if(!closed)Render();}
    }
    private FrameworkElement PaintFanCurves()
    {
        if(tuningDraft is not{} p)return Paint.Text("风扇曲线待读取。",12,muted:true);
        var cpu=Paint.Card(Paint.Stack(8,Paint.Text("CPU 风扇曲线"),new PaintFanEditor(p.CpuCurve,next=>{tuningDraft=tuningDraft! with{CpuCurve=next};PaintDraftChanged();RefreshPaintedState();})),14,12,true);
        var gpu=Paint.Card(Paint.Stack(8,Paint.Text("GPU 风扇曲线"),new PaintFanEditor(p.GpuCurve,next=>{tuningDraft=tuningDraft! with{GpuCurve=next};PaintDraftChanged();RefreshPaintedState();})),14,12,true);
        var grid=new DesktopTiles{PreferredColumns=2,MinimumItemWidth=0,ViewportBreakpoint=960,ColumnSpacing=16,RowSpacing=16};grid.Children.Add(cpu);grid.Children.Add(gpu);var host=new ContentControl{Content=grid,HorizontalContentAlignment=HorizontalAlignment.Stretch};paintBindings.Add(()=>host.IsEnabled=tuningDraft?.CustomFans==true&&!tuningApplying);return host;
    }
    private FrameworkElement PaintTools(string route)
    {
        var tools=new SuiteWrapPanel();if(route=="color")
        {
            tools.Children.Add(PB("导入",async()=>{if(!uiReviewMode)await PaintImportColor();}));tools.Children.Add(PB("导出",async()=>{if(colorDraft!=null&&!uiReviewMode)await SaveJsonFile("色彩方案.json",colorDraft.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));}));
            tools.Children.Add(PB("重置",async()=>{if(uiReviewMode||colorDraft==null||serviceColor.ValueKind!=JsonValueKind.Object)return;string? id=colorDraft["id"]?.GetValue<string>();var original=serviceColor.GetProperty("factory_presets").EnumerateArray().FirstOrDefault(p=>p.GetProperty("id").GetString()==id);if(original.ValueKind==JsonValueKind.Object){colorDraft=System.Text.Json.Nodes.JsonNode.Parse(original.GetRawText())!.AsObject();await SubmitColor();Render();}}));return tools;
        }
        foreach(var(label,command)in new[]{("新建","new"),("保存","save"),("恢复内置","restore"),("重命名","rename"),("导入","import"),("导出","export"),("打开目录","folder"),("删除","delete")})
        {string name=command;var button=PB(label,()=>PaintPresetAction(name));paintBindings.Add(()=>button.IsEnabled=tuningBaseline!=null&&tuningDraft!=null&&!tuningApplying&&(name!="restore"||SelectedTuningPreset?.BuiltIn==true)&&(name is not("rename" or "delete")||SelectedTuningPreset?.BuiltIn==false));tools.Children.Add(button);}return tools;
    }
    private async Task PaintPresetAction(string command)
    {
        if(tuningDraft==null||tuningBaseline is not{} state)return;
        TuningPreset Draft()=>SelectedTuningPreset is{} selected?selected with{Profile=tuningDraft.Copy()}:new("user_"+Guid.NewGuid().ToString("N"),"新方案","star",false,tuningDraft.Copy(),[]);
        if(uiReviewMode)return;
        switch(command)
        {
            case "new":string? name=await PaintPresetName("新建方案","自定义方案");if(name==null)return;var duplicate=tuningPresetStore.Duplicate(Draft(),name,state);RememberPresetDraft();tuningPresets.Add(duplicate);tuningPresetId=duplicate.Id;tuningDraft=duplicate.Profile.Copy();tuningStatus="方案已保存，尚未应用。";break;
            case "save":var p=Draft();bool created=SelectedTuningPreset==null;if(created){string? n=await PaintPresetName("保存方案",p.Name);if(n==null)return;p=p with{Name=n};}tuningPresetStore.Save(p,state);if(created){tuningPresets.Add(p);tuningPresetId=p.Id;}RememberPresetDraft();tuningStatus="方案已保存，尚未应用。";break;
            case "restore":var original=tuningBuiltinDefaults.First(p=>p.Id==tuningPresetId);tuningDraft=original.Profile.Copy();tuningPresets[tuningPresets.FindIndex(p=>p.Id==original.Id)]=original;tuningPresetStore.Save(original,state);tuningStatus="内置方案已恢复，尚未应用。";break;
            case "rename":var selected=SelectedTuningPreset!;string? renamed=await PaintPresetName("重命名方案",selected.Name);if(renamed==null)return;selected=selected with{Name=renamed};tuningPresetStore.Save(selected,state);tuningPresets[tuningPresets.FindIndex(p=>p.Id==selected.Id)]=selected;break;
            case "import":var open=new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id);open.FileTypeFilter.Add(".json");var file=await open.PickSingleFileAsync();if(file==null)return;if(new FileInfo(file.Path).Length>1024*1024)throw new IOException("方案文件不能超过1MiB。");var imported=tuningPresetStore.Import(await File.ReadAllTextAsync(file.Path),state);RememberPresetDraft();tuningPresets.Add(imported);tuningPresetId=imported.Id;tuningDraft=imported.Profile.Copy();tuningStatus="方案已导入，尚未应用。";break;
            case "export":var export=Draft();HardwareSession.ValidateTuning(state,export.Profile);await SaveJsonFile(export.Name+".json",TuningPresetStore.Export(export));break;
            case "folder":OpenLocalFolder(tuningPresetStore.DirectoryPath);return;
            case "delete":var deleted=SelectedTuningPreset!;if(!await Confirm("删除“"+deleted.Name+"”？","方案文件会移到trash目录，当前硬件参数保持。"))return;tuningPresetStore.Delete(deleted);tuningPresets.RemoveAll(p=>p.Id==deleted.Id);tuningPresetId=null;break;
        }
        AppData.Update(p=>p with{SelectedTuningPreset=tuningPresetId});Render();
    }
    private async Task PaintImportColor()
    {
        var open=new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id);open.FileTypeFilter.Add(".json");var file=await open.PickSingleFileAsync();if(file==null)return;if(new FileInfo(file.Path).Length>1024*1024)throw new IOException("方案文件不能超过1MiB。");if(colorDraft==null)throw new IOException("请先读取色彩预设。");
        var imported=System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(file.Path))!.AsObject();
        foreach(string id in new[]{"color-1-0","color-1-1","color-2-0","color-2-1","color-3-0","color-3-1","color-3-2"}){string key=PaintColorKey(id);double value=imported[key]?.GetValue<double>()??throw new IOException("色彩参数不完整。");var range=PaintRangeBinding(id);if(!double.IsFinite(value)||value<range.Min||value>range.Max)throw new IOException("色彩参数超出范围。");}
        foreach(string key in new[]{"color_temp_k","saturation","contrast","gamma","gain_r","gain_g","gain_b"})colorDraft[key]=imported[key]!.DeepClone();await SubmitColor();Render();
    }
}
