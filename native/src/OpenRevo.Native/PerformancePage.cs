using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core;
using OpenRevo.Core.Hal;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private string PerformanceName=>hardware.CurrentMode switch{1=>"办公",2=>"均衡",3=>"狂暴",4=>"自定义",_=>"待读取"};
    private async Task ApplySavedPerformance(int mode)
    {
        if(!hardware.Active||hardwareBusy||tuningApplying)return;
        if(mode!=4){await RunHardware(()=>hardware.ApplyModeCommand(upstream,mode,mode==3),"性能档位已回读确认。");return;}
        try
        {
            if(upstream==null)throw new IOException("设备未连接。");
            string? id=AppData.Load().SelectedTuningPreset??tuningPresetId;
            if(string.IsNullOrEmpty(id))throw new IOException("请通过齿轮选择并保存一个调校预设。");
            var snapshot=await Task.Run(()=>hardware.ProbeTuning(upstream));
            var originals=await Task.Run(()=>upstream.Read(18));
            var defaults=originals.EnumerateArray().Select(p=>TuningPresetStore.FromOriginal(p,snapshot,true)).ToArray();
            var preset=tuningPresetStore.LoadForApply(id,defaults,snapshot);
            HardwareSession.ValidateTuning(snapshot,preset.Profile);
            await RunHardware(()=>hardware.ApplyTuning(upstream,snapshot,preset.Profile.Copy()),"自定义预设已应用并回读确认。");
        }
        catch(Exception error){await ShowMessage("自定义模式未应用",error.Message);}
    }
    private FrameworkElement PerformanceChoices()
    {
        string[] labels=["办公","均衡","狂暴","自定义"],icons=["mode-office","mode-balanced","mode-performance","mode-custom"];
        var grid=new Grid{ColumnSpacing=6};performanceButtons.Clear();
        for(int i=0;i<4;i++)grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        for(int i=0;i<4;i++)
        {
            int mode=i+1;bool active=hardware.CurrentMode==mode;
            var button=Ui.Button(labels[i],async(_,_)=>await ApplySavedPerformance(mode));var body=Ui.Stack(ApprovedIcons.Create(icons[i],mini?28:mode==4?34:24,active,mini||mode!=4),Ui.Text(labels[i],12));body.Spacing=4;button.Content=body;
            button.Padding=new Thickness(3);button.MinWidth=0;button.Height=mini?56:64;button.HorizontalAlignment=HorizontalAlignment.Stretch;button.HorizontalContentAlignment=HorizontalAlignment.Center;
            button.Tag="detail-choice";performanceButtons[mode]=button;overviewModeAccent[mode]=active;SetChoiceVisual(button,active);
            FrameworkElement content=button;
            if(mode==4)
            {
                var cell=new Grid();cell.Children.Add(button);var gear=Ui.Button("编辑自定义预设",(_,_)=>OpenTuning());gear.Content=ApprovedIcons.Utility("settings",16);gear.Width=gear.Height=24;gear.MinWidth=gear.MinHeight=0;gear.Padding=new Thickness(0);gear.HorizontalAlignment=HorizontalAlignment.Right;gear.VerticalAlignment=VerticalAlignment.Top;gear.Background=new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);gear.BorderThickness=new Thickness(0);cell.Children.Add(gear);content=cell;
            }
            Grid.SetColumn(content,i);grid.Children.Add(content);
        }
        return grid;
    }
}
