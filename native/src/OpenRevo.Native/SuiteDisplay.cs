using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private NumberBox? approvedBrightnessNumber;
    private Button? approvedRefreshChoice;
    private FrameworkElement SuiteBrightness()
    {
        brightnessSlider=new Slider{Minimum=10,Maximum=100,StepFrequency=1,Value=screenBrightness??10,MinHeight=32,IsEnabled=displayState?.Active==true&&screenBrightness.HasValue};AttachBrightnessEvents(brightnessSlider);
        approvedBrightnessNumber=new NumberBox{Minimum=10,Maximum=100,Value=screenBrightness??double.NaN,Width=84,FontSize=14,IsEnabled=brightnessSlider.IsEnabled,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Hidden};
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(approvedBrightnessNumber,"屏幕亮度数值");
        approvedBrightnessNumber.ValueChanged+=async(_,e)=>{if(!syncingBrightness&&double.IsFinite(e.NewValue))await SetCompactBrightness((int)Math.Round(e.NewValue));};
        var value=SettingsControls(approvedBrightnessNumber,Ui.Text("%",12,muted:true));
        var scale=Ui.Row(Ui.Text("10 %",12,muted:true),Ui.Text("100 %",12,muted:true));
        return SuiteVisuals.Rule(SuiteVisuals.Stack(8,Ui.Row(Ui.Text("屏幕亮度"),value),BrightnessTrack(brightnessSlider),scale),14);
    }
    private FrameworkElement SuiteRefreshChoice()
    {
        var label=Ui.Text(displayState?.Active==true?displayState.Hertz+" Hz":"—");live["approvedRefreshValue"]=label;
        var button=approvedRefreshChoice=Ui.Button("屏幕刷新率",(_,_)=>{});button.Content=SettingsControls(label,ApprovedIcons.Utility("chevron",13));
        var flyout=new MenuFlyout();
        foreach(int rate in (displayState?.Rates??[]).Distinct().Order())
        {
            var item=new ToggleMenuFlyoutItem{Text=rate+" Hz",IsChecked=displayState?.Hertz==rate};item.Click+=async(_,_)=>await ChangeRefresh(rate);flyout.Items.Add(item);
        }
        flyout.Opening+=(_,_)=>{foreach(var item in flyout.Items.OfType<ToggleMenuFlyoutItem>())item.IsChecked=item.Text==displayState?.Hertz+" Hz";};
        button.Flyout=flyout;button.IsEnabled=displayState?.Active==true&&flyout.Items.Count>0;return button;
    }
    private FrameworkElement SuiteDisplayOptions()
    {
        var presets=new List<FrameworkElement>();foreach(int value in new[]{25,60,100}){var b=Ui.Button(value+"%",async(_,_)=>await SetCompactBrightness(value));brightnessButtons[value]=b;presets.Add(b);}
        var auto=ServiceToggle("拔电自动低刷",lowRefreshOnBattery,async(sender,_)=>{lowRefreshOnBattery=((ToggleSwitch)sender).IsOn;await SavePreferences();await CheckAutomaticRefresh(true);ConfirmedActionOsd("拔电自动低刷 · "+(lowRefreshOnBattery?"已开启":"已关闭"),"refresh");});
        return SuiteVisuals.Stack(0,SuiteBrightness(),SuiteSettingRow("亮度预设","",SettingsControls(presets.ToArray())),SuiteSettingRow("屏幕刷新率","真实产品仅列出本机支持的频率。",SuiteRefreshChoice()),SuiteSettingRow("拔电自动低刷","恢复接电后按既有策略处理。",auto));
    }
    private void RenderSuiteColorControls()
    {
        colorServiceToggle=ServiceToggle("屏幕色彩增强",false,async(_,_)=>{if(!syncingColorServices)await SubmitColor();});
        colorEditorButton=Ui.Button("查看 ›",(_,_)=>{colorTuningOpen=true;customTuningOpen=false;Render();});SuiteVisuals.Flat(colorEditorButton,34);
        colorServiceStatus=Ui.Text("状态待读取",12,muted:true);
        iccProfileChoice=new ComboBox{MinWidth=150,MaxWidth=210};Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(iccProfileChoice,"ICC 配置");
        iccProfileChoice.SelectionChanged+=async(_,_)=>{if(!syncingColorServices&&iccProfileChoice.SelectedItem is ServiceChoice selected){if(selected.Id!="default_native")lastIccProfile=selected.Id;await ApplyIcc(selected.Id);}};
        iccImport=Ui.Button("选择文件",async(_,_)=>await ImportIcc());iccServiceStatus=Ui.Text("状态待读取",12,muted:true);
        page.Children.Add(SuiteVisuals.Stack(0,SuiteSettingRow("屏幕色彩增强","全局滤镜，影响所有显示器。",colorServiceToggle),SubpageColorLink(),SuiteSettingRow("ICC 配置","物理内屏；列表来自本机配置。",iccProfileChoice),SuiteSettingRow("导入 ICC","保留文件验证和取消行为。",iccImport),colorServiceStatus,iccServiceStatus));SyncColorServices();
    }
    private Border SubpageColorLink()=>SuiteSettingRow("调色控制","色温、饱和度、对比度与 Gamma",colorEditorButton!);
}
