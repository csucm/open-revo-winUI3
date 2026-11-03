using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    // Copy and icon IDs from the fixed React GpuModeCards. Both panels use this table.
    private static readonly (int Mode,string Title,string Icon,string Chip,string Description)[] originalGpuModes =
    [
        (3,"独显直连","zap","dGPU","独显直接驱动内屏，减少画面中转。适合接电游戏，功耗通常更高。"),
        (2,"混合输出","layers","HYBRID","轻载由核显输出，游戏和图形应用可调用独显渲染。兼顾续航和性能。"),
        (1,"核显模式","power-off","iGPU","由集成显卡驱动内屏。适合办公、浏览和电池供电。")
    ];
    private static string OriginalGpuName(int mode)=>originalGpuModes.FirstOrDefault(m=>m.Mode==mode).Title??"待读取";
    private static FrameworkElement OriginalDeviceGraphic(string name,double size=18)
    {
        return ApprovedIcons.Create(name switch{"plug"=>"charging","sun"=>"brightness","power-off"=>"gpu-integrated","layers"=>"gpu-hybrid","zap"=>"gpu-discrete","battery"=>"battery","keyboard"=>"keyboard","power"=>"power",_=>"lighting"},size);
    }
    private static StackPanel OriginalIconLabel(string icon,string text,double size=12)
    {
        var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8,VerticalAlignment=VerticalAlignment.Center};
        row.Children.Add(OriginalDeviceGraphic(icon));row.Children.Add(Ui.Text(text,size));return row;
    }
    private Button OriginalGpuCard(int mode,string title,string icon,string chip,string description)
    {
        var graphic=ApprovedIcons.Create(mode==1?"gpu-integrated":mode==2?"gpu-hybrid":"gpu-discrete",34);
        var name=Ui.Text(title,14);name.TextAlignment=TextAlignment.Center;
        var hint=Ui.Text(description,12,muted:true);hint.TextAlignment=TextAlignment.Center;
        var body=Ui.Stack(graphic,name,hint);body.Spacing=8;graphic.HorizontalAlignment=HorizontalAlignment.Center;
        var button=Ui.Button(title,async(_,_)=>await RunGpuRequest(mode));button.Content=body;button.Tag="detail-choice";
        button.MinHeight=175;button.Padding=new Thickness(14,12,14,12);button.CornerRadius=new CornerRadius(10);
        // A shared row slot is insufficient if a Button keeps Center alignment.
        button.VerticalAlignment=VerticalAlignment.Stretch;button.HorizontalAlignment=HorizontalAlignment.Stretch;
        button.VerticalContentAlignment=VerticalAlignment.Stretch;button.HorizontalContentAlignment=HorizontalAlignment.Stretch;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button,title+"。"+description);
        ToolTipService.SetToolTip(button,description+"\n保存下次启动设置；不自动重启。支持情况以实际硬件回读为准。");return button;
    }
}
