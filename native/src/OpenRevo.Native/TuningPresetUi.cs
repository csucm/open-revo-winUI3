using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core;
using OpenRevo.Core.Hal;
using System.Diagnostics;
using System.Text.Json;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private readonly TuningPresetStore tuningPresetStore=new();
    private readonly List<TuningPreset> tuningPresets=[];
    private TuningPreset[] tuningBuiltinDefaults=[];
    private string? tuningPresetId;
    private TuningPreset? SelectedTuningPreset=>tuningPresets.FirstOrDefault(p=>p.Id==tuningPresetId);
    private void InitializeTuningPresets(JsonElement originals,TuningSnapshot state)
    {
        var defaults=originals.EnumerateArray().Select(p=>TuningPresetStore.FromOriginal(p,state,true)).ToArray();
        tuningBuiltinDefaults=defaults;tuningPresets.Clear();tuningPresets.AddRange(tuningPresetStore.Load(defaults,state));tuningPresetId=null;
    }
    private void RememberPresetDraft()
    {
        int index=tuningPresets.FindIndex(p=>p.Id==tuningPresetId);
        if(index>=0&&tuningDraft!=null)tuningPresets[index]=tuningPresets[index] with{Profile=tuningDraft.Copy()};
    }
    private void RefreshTuningPanel(){if(!closed&&tuningPanel!=null)FillTuningEditor(tuningPanel);}
    private async Task<string?> AskPresetName(string title,string initial)
    {
        var input=new TextBox{Text=initial,MaxLength=12,PlaceholderText="1–12个字符"};
        var dialog=new ContentDialog{XamlRoot=root.XamlRoot,Title=title,Content=input,PrimaryButtonText="保存",CloseButtonText="取消",DefaultButton=ContentDialogButton.Primary};
        return await dialog.ShowAsync()==ContentDialogResult.Primary?TuningPresetStore.ValidName(input.Text):null;
    }
    private ContentControl TuningPresetToolbar(StackPanel host,TuningSnapshot state)
    {
        var choice=new ComboBox{MinWidth=180,HorizontalAlignment=HorizontalAlignment.Stretch,ItemsSource=tuningPresets.ToArray(),SelectedItem=SelectedTuningPreset,PlaceholderText="当前硬件参数（未选方案）"};
        choice.SelectionChanged+=(_,_)=>
        {
            if(tuningApplying||choice.SelectedItem is not TuningPreset selected)return;
            RememberPresetDraft();tuningPresetId=selected.Id;tuningDraft=selected.Profile.Copy();tuningStatus="已载入“"+selected.Name+"”，尚未应用。";RefreshTuningPanel();
        };
        var leading=new Grid{ColumnSpacing=10,VerticalAlignment=VerticalAlignment.Center};leading.ColumnDefinitions.Add(new(){Width=GridLength.Auto});leading.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        var icon=SemanticGraphic(SelectedTuningPreset?.Icon??"tuning",24);leading.Children.Add(icon);Grid.SetColumn(choice,1);leading.Children.Add(choice);
        var tools=new StackPanel{Orientation=Orientation.Horizontal,Spacing=4};
        Button Tool(string glyph,string title,Func<Task> action,bool enabled=true)
        {
            var button=Ui.Button(title,async(_,_)=>{try{await action();}catch(Exception error){if(!closed)await ShowMessage("方案操作未完成",error.Message);}});
            button.Content=DetailIcon(glyph,16);button.Padding=new Thickness(8);button.Width=36;button.Height=36;button.IsEnabled=enabled;ToolTipService.SetToolTip(button,title);tools.Children.Add(button);return button;
        }
        TuningPreset DraftPreset()=>SelectedTuningPreset is{} p?p with{Profile=tuningDraft!.Copy()}:new("user_"+Guid.NewGuid().ToString("N"),"新方案","star",false,tuningDraft!.Copy(),[]);
        Tool("\uE710","新建方案（复制当前草稿）",async()=>
        {
            string? name=await AskPresetName("新建方案","自定义方案");if(name==null)return;
            var created=tuningPresetStore.Duplicate(DraftPreset(),name,state);RememberPresetDraft();tuningPresets.Add(created);tuningPresetId=created.Id;tuningDraft=created.Profile.Copy();tuningStatus="方案已保存，尚未应用。";RefreshTuningPanel();
        });
        Tool("\uE74E","保存当前方案",async()=>
        {
            var p=DraftPreset();bool create=SelectedTuningPreset==null;if(create){string? name=await AskPresetName("保存方案",p.Name);if(name==null)return;p=p with{Name=name};}
            tuningPresetStore.Save(p,state);if(create){tuningPresets.Add(p);tuningPresetId=p.Id;}RememberPresetDraft();tuningStatus="方案已保存到本机，尚未应用。";RefreshTuningPanel();
        });
        Tool("\uE777","恢复当前内置方案",()=>
        {
            var original=tuningBuiltinDefaults.First(p=>p.Id==tuningPresetId);tuningDraft=original.Profile.Copy();int index=tuningPresets.FindIndex(p=>p.Id==original.Id);tuningPresets[index]=original;tuningPresetStore.Save(original,state);tuningStatus="已恢复本机适配后的内置方案，尚未应用。";RefreshTuningPanel();return Task.CompletedTask;
        },SelectedTuningPreset?.BuiltIn==true);
        Tool("\uE70F","重命名方案",async()=>
        {
            var p=DraftPreset();string? name=await AskPresetName("重命名方案",p.Name);if(name==null)return;p=p with{Name=name};tuningPresetStore.Save(p,state);tuningPresets[tuningPresets.FindIndex(x=>x.Id==p.Id)]=p;tuningStatus="方案已重命名，未写入硬件。";RefreshTuningPanel();
        },SelectedTuningPreset?.BuiltIn==false);
        Tool("\uE898","导入 JSON 方案",async()=>
        {
            var picker=new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id);picker.FileTypeFilter.Add(".json");var file=await picker.PickSingleFileAsync();if(file==null)return;
            if(new FileInfo(file.Path).Length>1024*1024)throw new IOException("方案文件不能超过1MiB。");
            var imported=tuningPresetStore.Import(await File.ReadAllTextAsync(file.Path),state);RememberPresetDraft();tuningPresets.Add(imported);tuningPresetId=imported.Id;tuningDraft=imported.Profile.Copy();tuningStatus="方案已导入并检查，尚未应用。";RefreshTuningPanel();
        });
        Tool("\uE896","导出 JSON 方案",async()=>{var p=DraftPreset();HardwareSession.ValidateTuning(state,p.Profile);await SaveJsonFile(p.Name+".json",TuningPresetStore.Export(p));});
        Tool("\uE8B7","打开方案文件夹",()=>
        {
            Directory.CreateDirectory(tuningPresetStore.DirectoryPath);var start=new ProcessStartInfo("explorer.exe"){UseShellExecute=true};start.ArgumentList.Add(tuningPresetStore.DirectoryPath);Process.Start(start);return Task.CompletedTask;
        });
        Tool("\uE74D","删除自定义方案",async()=>
        {
            var selected=SelectedTuningPreset!;var dialog=new ContentDialog{XamlRoot=root.XamlRoot,Title="删除“"+selected.Name+"”？",Content="文件会移到方案文件夹内的 trash 子目录。当前硬件参数保持。",PrimaryButtonText="删除",CloseButtonText="取消"};
            if(await dialog.ShowAsync()!=ContentDialogResult.Primary)return;
            tuningPresetStore.Delete(selected);tuningPresets.RemoveAll(p=>p.Id==selected.Id);tuningPresetId=null;tuningStatus="方案已删除，当前草稿保留。";RefreshTuningPanel();
        },SelectedTuningPreset?.BuiltIn==false);
        var row=DesktopAlignedRow(leading,tools);var card=Ui.Card(row);card.Padding=new Thickness(12);return new ContentControl{Content=card,HorizontalContentAlignment=HorizontalAlignment.Stretch};
    }
    private async Task SaveJsonFile(string name,string content)
    {
        var picker=new Microsoft.Windows.Storage.Pickers.FileSavePicker(AppWindow.Id){SuggestedFileName=Path.GetFileNameWithoutExtension(name)};
        picker.FileTypeChoices.Add("JSON 方案",new List<string>{".json"});var file=await picker.PickSaveFileAsync();if(file==null)return;
        await File.WriteAllTextAsync(file.Path,content);
    }
}
