using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private Border? desktopBatteryFill;
    private float desktopBatteryRatio=-1;
    private FrameworkElement? desktopBatteryAcIcon,desktopBatteryDcIcon;
    private FrameworkElement BatteryGauge()
    {
        desktopBatteryRatio=-1;
        var picture=new Canvas{Width=160,Height=50};
        var shell=(Border)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Width='150' Height='48' BorderBrush='{ThemeResource OverviewMuted}' BorderThickness='2' CornerRadius='6' Background='{ThemeResource DesignInsetFill}'/>");Canvas.SetLeft(shell,1);Canvas.SetTop(shell,1);picture.Children.Add(shell);
        desktopBatteryFill=new Border{Width=140,Height=38,CornerRadius=new CornerRadius(3)};Canvas.SetLeft(desktopBatteryFill,6);Canvas.SetTop(desktopBatteryFill,6);picture.Children.Add(desktopBatteryFill);
        var tip=(Border)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Width='5' Height='16' Background='{ThemeResource OverviewMuted}' CornerRadius='2'/>");Canvas.SetLeft(tip,154);Canvas.SetTop(tip,17);picture.Children.Add(tip);
        var amount=Value("approvedBatteryAmount",24);amount.FontWeight=Microsoft.UI.Text.FontWeights.Normal;
        var amountRow=SettingsControls(amount,Ui.Text("%",14));amountRow.Spacing=3;
        var status=Value("batteryDetail",12);status.FontWeight=Microsoft.UI.Text.FontWeights.Normal;
        var limit=Value("approvedHealthLimit",12);limit.FontWeight=Microsoft.UI.Text.FontWeights.Normal;
        var text=SuiteVisuals.Stack(6,Ui.Text("剩余电量",12,muted:true),amountRow,status,limit);
        var body=new Grid{ColumnSpacing=24,Padding=new Thickness(16),MaxWidth=436,HorizontalAlignment=HorizontalAlignment.Stretch,VerticalAlignment=VerticalAlignment.Center};body.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star),MaxWidth=220});body.ColumnDefinitions.Add(new(){Width=GridLength.Auto,MinWidth=120,MaxWidth=160});
        body.Children.Add(new Viewbox{Child=picture,MaxWidth=220,Stretch=Stretch.Uniform});Grid.SetColumn(text,1);body.Children.Add(text);
        desktopBatteryFill.Loaded+=(_,_)=>UpdateOriginalBatteryGauge();return body;
    }
    private void UpdateOriginalBatteryGauge()
    {
        if(desktopBatteryFill==null||!desktopBatteryFill.IsLoaded)return;
        int? percent=latest?.BatteryPercent;
        // Original OEM shell: retain a 5% visible sliver at a known low charge.
        // Unknown data stays empty and is labelled unavailable by the live text.
        float ratio=percent.HasValue?Math.Clamp(percent.Value,5,100)/100f:0;
        if(Math.Abs(desktopBatteryRatio-ratio)>.001)
        {
            var visual=ElementCompositionPreview.GetElementVisual(desktopBatteryFill);visual.CenterPoint=Vector3.Zero;
            if(Design.MotionEnabled&&desktopBatteryRatio>=0)MotionDesign.Scale(visual,new(ratio,1,1),MotionDesign.Data);
            else{visual.StopAnimation("Scale");visual.Scale=new(ratio,1,1);}
            desktopBatteryRatio=ratio;
        }
        string brush=percent is <=20?"SemanticRed":percent is <=50?"SemanticAmber":"SemanticGreen";
        var palette=(ResourceDictionary)Application.Current.Resources.ThemeDictionaries[root.ActualTheme==ElementTheme.Dark?"Default":"Light"];
        desktopBatteryFill.Background=(Brush)palette[brush];
        if(desktopBatteryAcIcon!=null)desktopBatteryAcIcon.Visibility=latest?.OnAc==true?Visibility.Visible:Visibility.Collapsed;
        if(desktopBatteryDcIcon!=null)desktopBatteryDcIcon.Visibility=latest?.OnAc==false?Visibility.Visible:Visibility.Collapsed;
    }
}

internal sealed class ApprovedBatteryLayout:Panel
{
    private readonly FrameworkElement presets,gauge;
    private readonly Border divider;
    private bool narrow;
    internal ApprovedBatteryLayout(FrameworkElement presets,FrameworkElement gauge)
    {
        this.presets=presets;this.gauge=gauge;
        divider=(Border)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Background='{ThemeResource DesignStroke}'/>");
        Children.Add(presets);Children.Add(gauge);Children.Add(divider);
    }
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size available)
    {
        double width=double.IsFinite(available.Width)?available.Width:800;narrow=XamlRoot?.Size.Width<=960;
        double column=narrow?width:Math.Max(0,(width-16)/2);presets.Measure(new(column,double.PositiveInfinity));gauge.Measure(new(column,double.PositiveInfinity));divider.Measure(new(1,1));
        return new(width,narrow?presets.DesiredSize.Height+16+gauge.DesiredSize.Height:Math.Max(presets.DesiredSize.Height,gauge.DesiredSize.Height));
    }
    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size final)
    {
        if(narrow){presets.Arrange(new(0,0,final.Width,presets.DesiredSize.Height));gauge.Arrange(new(0,presets.DesiredSize.Height+16,final.Width,gauge.DesiredSize.Height));divider.Arrange(new(0,0,0,0));}
        else {double column=Math.Max(0,(final.Width-16)/2);presets.Arrange(new(0,0,column,final.Height));gauge.Arrange(new(column+16,0,column,final.Height));divider.Arrange(new(column+7.5,0,1,final.Height));}return final;
    }
}
