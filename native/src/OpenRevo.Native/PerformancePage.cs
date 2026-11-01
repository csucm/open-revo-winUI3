using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private string PerformanceName => hardware.CurrentMode switch {1=>"办公",2=>"均衡",3=>"狂暴",4=>"极客调校",_=>"读取中"};
    private FrameworkElement PerformanceChoices()
    {
        string[] labels=["办公","均衡","狂暴","自定义"];
        string[] icons=["","","",""];
        var grid=new Grid {ColumnSpacing=6,RowSpacing=8};
        performanceButtons.Clear();
        for(int i=0;i<(mini?4:2);i++)grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        for(int i=0;i<4;i++)
        {
            int mode=i+1;
            var button=mode==4 ? Ui.Button(icons[i]+"\n"+labels[i],(_,_)=>OpenTuning())
                : MiniControl(icons[i]+"\n"+labels[i],()=>hardware.ApplyModeCommand(upstream,mode,mode==3),
                    mode switch { 1 => "降低功耗和风扇噪声，适合办公", 2 => "兼顾性能与噪声，适合日常使用", _ => "优先发挥性能，需连接电源，风扇会更响" });
            performanceButtons[mode] = button;
            if(mode==4)
            {
                button.Padding=new Thickness(4);button.FontSize=mini?12:14;
                if(mini&&button.Content is StackPanel content)
                {
                    content.Spacing=2;
                    foreach(var child in content.Children.OfType<FrameworkElement>())
                        if(child is TextBlock text)text.FontSize=11;
                        else{child.Width=15;child.Height=15;}
                }
            }
            if(mode==4)ToolTipService.SetToolTip(button,"打开功耗、温控、显卡与风扇调校，点击应用后生效");
            button.HorizontalAlignment=HorizontalAlignment.Stretch;button.Height=mini?44:84;
            if(hardware.CurrentMode==mode)button.Style=(Style)Application.Current.Resources["SelectedChoiceStyle"];
            if(!mini && i%2==0)grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
            Grid.SetColumn(button,mini?i:i%2);Grid.SetRow(button,mini?0:i/2);grid.Children.Add(button);
        }
        return grid;
    }
}
