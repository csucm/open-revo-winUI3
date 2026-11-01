using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private Border? desktopBatteryFill;
    private Storyboard? batteryGaugeAnimation;
    private FrameworkElement? desktopBatteryAcIcon,desktopBatteryDcIcon;
    private FrameworkElement BatteryGauge()
    {
        desktopBatteryFill=new Border{CornerRadius=new CornerRadius(3),HorizontalAlignment=HorizontalAlignment.Left,Width=0};
        var shell=(Border)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Width='150' Height='48' BorderBrush='{ThemeResource TextFillColorSecondaryBrush}' BorderThickness='2' CornerRadius='6' Padding='3' Background='{ThemeResource DesignInsetFill}'/>");
        shell.Child=desktopBatteryFill;
        var tip=(Border)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Width='5' Height='16' Background='{ThemeResource TextFillColorSecondaryBrush}' CornerRadius='0,3,3,0' VerticalAlignment='Center'/>");
        var picture=new StackPanel{Orientation=Orientation.Horizontal,Spacing=3,HorizontalAlignment=HorizontalAlignment.Center};picture.Children.Add(shell);picture.Children.Add(tip);
        var amount=Value("battery",28);amount.TextAlignment=TextAlignment.Center;
        var status=Value("batteryDetail",12);status.TextAlignment=TextAlignment.Center;status.VerticalAlignment=VerticalAlignment.Center;
        desktopBatteryAcIcon=OriginalDeviceGraphic("plug",14);desktopBatteryDcIcon=OriginalDeviceGraphic("battery",14);
        var icons=new Grid{Width=14,Height=14,VerticalAlignment=VerticalAlignment.Center};icons.Children.Add(desktopBatteryAcIcon);icons.Children.Add(desktopBatteryDcIcon);
        var statusRow=new Grid{ColumnSpacing=5,HorizontalAlignment=HorizontalAlignment.Center};statusRow.ColumnDefinitions.Add(new(){Width=GridLength.Auto});statusRow.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});statusRow.Children.Add(icons);Grid.SetColumn(status,1);statusRow.Children.Add(status);
        var body=Ui.Stack(picture,amount,statusRow);body.Spacing=6;body.VerticalAlignment=VerticalAlignment.Center;
        UpdateOriginalBatteryGauge();var card=DetailInset(body);card.Padding=new Thickness(16,12,16,12);card.VerticalAlignment=VerticalAlignment.Stretch;return card;
    }
    private void UpdateOriginalBatteryGauge()
    {
        if(desktopBatteryFill==null)return;
        int? percent=latest?.BatteryPercent;
        // Original OEM shell: retain a 5% visible sliver at a known low charge.
        // Unknown data stays empty and is labelled unavailable by the live text.
        double width=percent.HasValue?140*Math.Clamp(percent.Value,5,100)/100.0:0;
        double from=desktopBatteryFill.ActualWidth;
        if(Math.Abs(desktopBatteryFill.Width-width)>.1)
        {
            batteryGaugeAnimation?.Stop();desktopBatteryFill.Width=width;
            if(Design.MotionEnabled&&from>0&&Math.Abs(from-width)>.1)
            {
                var animation=new DoubleAnimation{From=from,To=width,Duration=TimeSpan.FromMilliseconds(400),EasingFunction=new CubicEase{EasingMode=EasingMode.EaseInOut}};
                Storyboard.SetTarget(animation,desktopBatteryFill);Storyboard.SetTargetProperty(animation,"Width");
                batteryGaugeAnimation=new Storyboard();batteryGaugeAnimation.Children.Add(animation);batteryGaugeAnimation.Begin();
            }
        }
        string brush=percent is <=20?"SemanticRed":percent is <=50?"SemanticAmber":"SemanticGreen";
        var palette=(ResourceDictionary)Application.Current.Resources.ThemeDictionaries[root.ActualTheme==ElementTheme.Dark?"Default":"Light"];
        desktopBatteryFill.Background=(Brush)palette[brush];
        desktopBatteryAcIcon!.Visibility=latest?.OnAc==true?Visibility.Visible:Visibility.Collapsed;
        desktopBatteryDcIcon!.Visibility=latest?.OnAc==false?Visibility.Visible:Visibility.Collapsed;
    }
}
