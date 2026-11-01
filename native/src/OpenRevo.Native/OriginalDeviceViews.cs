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
        (3,"独显直连","zap","dGPU","独立显卡物理直连内置屏幕。避开核显中转，发挥极致电竞帧率与超低延迟。"),
        (2,"混合输出","layers","HYBRID","轻载时核显输出以省电静音，高负载 3D 图形与游戏时自动介入独显加速。"),
        (1,"核显模式","power-off","iGPU","彻底切断独立显卡 PCIe 供电与待机功耗，极致降低整机能耗与发热，极大提升续航。")
    ];
    private static string OriginalGpuName(int mode)=>originalGpuModes.FirstOrDefault(m=>m.Mode==mode).Title??"待读取";
    private static FrameworkElement OriginalDeviceGraphic(string name,double size=18)
    {
        var icon=NavigationOriginalIcon("device-"+name);icon.Width=size;icon.Height=size;return icon;
    }
    private static StackPanel OriginalIconLabel(string icon,string text,double size=12)
    {
        var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8,VerticalAlignment=VerticalAlignment.Center};
        row.Children.Add(OriginalDeviceGraphic(icon));row.Children.Add(Ui.Text(text,size));return row;
    }
    private Button OriginalGpuCard(int mode,string title,string icon,string chip,string description)
    {
        var chipBody=Ui.Stack(OriginalDeviceGraphic(icon,16),Ui.Text(chip,12,true));chipBody.Spacing=2;
        chipBody.HorizontalAlignment=HorizontalAlignment.Center;chipBody.VerticalAlignment=VerticalAlignment.Center;
        var chipFrame=new Grid{Width=52,Height=52,HorizontalAlignment=HorizontalAlignment.Center};
        chipFrame.Children.Add(new Rectangle{RadiusX=8,RadiusY=8,Stroke=(Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],StrokeThickness=2,StrokeDashArray=new DoubleCollection{3,2}});
        chipFrame.Children.Add(chipBody);
        var hint=Ui.Text(description,12,muted:true);hint.TextAlignment=TextAlignment.Center;hint.VerticalAlignment=VerticalAlignment.Center;hint.Margin=new Thickness(0,8,0,8);
        var name=Ui.Text(title,14,true);name.TextAlignment=TextAlignment.Center;
        var body=new Grid();body.RowDefinitions.Add(new(){Height=GridLength.Auto});body.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});body.RowDefinitions.Add(new(){Height=GridLength.Auto});
        body.Children.Add(chipFrame);Grid.SetRow(hint,1);body.Children.Add(hint);Grid.SetRow(name,2);body.Children.Add(name);
        var button=Ui.Button(title,async(_,_)=>await RunGpuRequest(mode));button.Content=body;button.Tag="detail-choice";
        button.MinHeight=175;button.Padding=new Thickness(14,12,14,12);button.CornerRadius=new CornerRadius(10);
        // A shared row slot is insufficient if a Button keeps Center alignment.
        button.VerticalAlignment=VerticalAlignment.Stretch;button.HorizontalAlignment=HorizontalAlignment.Stretch;
        button.VerticalContentAlignment=VerticalAlignment.Stretch;button.HorizontalContentAlignment=HorizontalAlignment.Stretch;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button,title+"。"+description);
        ToolTipService.SetToolTip(button,description+"\n保存下次启动设置；不自动重启。支持情况以实际硬件回读为准。");return button;
    }
}
