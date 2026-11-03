using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private Slider? brightnessSlider;
    private bool brightnessEditing;
    private bool brightnessApplying;
    private bool syncingBrightness;
    private int? requestedBrightness;
    private Task? brightnessWork;
    private Func<int,Task> writeBrightness=value=>Task.Run(()=>OpenRevo.Core.ScreenBrightness.Set(value));
    private readonly Dictionary<int,Button> brightnessButtons=[];
    private ContentControl? miniRefreshHost;
    private Button? refreshMoreButton;
    private readonly Dictionary<int,ToggleMenuFlyoutItem> refreshMenuItems=[];
    private string miniRatesSignature="";
    private FrameworkElement MiniRefreshControls()
    {
        miniRefreshHost=new ContentControl{HorizontalContentAlignment=HorizontalAlignment.Stretch};
        miniRatesSignature="";
        UpdateMiniRefreshControls();
        return miniRefreshHost;
    }
    private void UpdateMiniRefreshControls()
    {
        if(miniRefreshHost==null)return;
        int[] rates=(displayState?.Rates??[]).Where(r=>r>1).Distinct().Order().ToArray();
        string signature="rates:"+string.Join(',',rates);
        if(signature!=miniRatesSignature)
        {
            miniRatesSignature=signature;
            miniRefreshHost.Content=BuildMiniRefreshControls(rates);
        }
        bool enabled=displayState?.Active==true&&!displayBusy;
        foreach(var(rate,item)in refreshMenuItems)
        {item.IsEnabled=enabled;item.IsChecked=enabled&&displayState!.Hertz==rate;}
        if(refreshMoreButton!=null)
        {
            refreshMoreButton.IsEnabled=enabled;
            bool selected=displayState?.Active==true&&refreshMenuItems.ContainsKey(displayState.Hertz);
            refreshMoreButton.Content=selected?$"{displayState!.Hertz} Hz ▾":"更多 ▾";
            if(selected)refreshMoreButton.Style=(Style)Application.Current.Resources["SelectedChoiceStyle"];else refreshMoreButton.ClearValue(FrameworkElement.StyleProperty);
        }
    }
    private FrameworkElement BuildMiniRefreshControls(int[] rates)
    {
        Button Rate(int rate)
        {
            var button = Ui.Button($"{rate} Hz", async (_, _) => await ChangeRefresh(rate));
            refreshButtons[rate] = button;
            button.Padding = new Thickness(4, 6, 4, 6); button.FontSize = 12;
            button.IsEnabled = displayState?.Active == true && displayState.Rates.Contains(rate) && !displayBusy;
            if (displayState?.Active == true && displayState.Hertz == rate) button.Style = (Style)Application.Current.Resources["SelectedChoiceStyle"];
            ToolTipService.SetToolTip(button, displayState?.Active == true ? "内置屏刷新率" : "内置屏未激活，远程会话中仅展示屏幕参数");
            return button;
        }
        refreshButtons.Clear();
        refreshMenuItems.Clear();refreshMoreButton=null;
        var controls=new List<FrameworkElement>{LowRefreshButton()};
        if(rates.Length==0)
        {
            if(controls[0] is Control low)low.IsEnabled=false;
            foreach(string label in new[]{"最低刷新率","最高刷新率"})
            {
                var pending=Ui.Button(label,(_,_)=>{});pending.IsEnabled=false;
                pending.Padding=new Thickness(4,6,4,6);pending.FontSize=12;
                ToolTipService.SetToolTip(pending,"尚未识别可用刷新率");controls.Add(pending);
            }
        }
        else
        {
            controls.Add(Rate(rates[0]));
            if(rates.Length>1)controls.Add(Rate(rates[^1]));
        }
        var menu = new MenuFlyout();
        foreach (int rate in rates.Skip(1).Take(Math.Max(0,rates.Length-2)))
        {
            var item = new ToggleMenuFlyoutItem { Text=$"{rate} Hz", FontSize=12, IsEnabled=displayState?.Active==true };
            item.Click += async (_,_) => {item.IsChecked=displayState?.Active==true&&displayState.Hertz==rate;await ChangeRefresh(rate);}; menu.Items.Add(item);refreshMenuItems[rate]=item;
        }
        if(refreshMenuItems.Count>0)
        {
            refreshMoreButton=new Button{Content="更多 ▾",Flyout=menu,FontSize=12,Padding=new Thickness(4,6,4,6)};
            ToolTipService.SetToolTip(refreshMoreButton,"内置屏实际支持的其他刷新率");controls.Add(refreshMoreButton);
        }
        return Columns(controls.ToArray());
    }
    private FrameworkElement MiniBrightnessCard()
    {
        var title = Ui.Text($"☼  屏幕亮度：{screenBrightness?.ToString() ?? "—"}%", 12, true);
        live["brightnessTitle"] = title;
        var presets = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (label, value) in new[] { ("暗光 25%", 25), ("室内 60%", 60), ("户外 100%", 100) })
        {
            var button = Ui.Button(label, async (_, _) => await SetCompactBrightness(value));
            button.FontSize = 10; button.Padding = new Thickness(4, 1, 4, 1); button.MinHeight = 24;
            button.IsEnabled = displayState?.Active == true && screenBrightness.HasValue && !displayBusy;
            brightnessButtons[value]=button;
            if(screenBrightness==value)button.Style=(Style)Application.Current.Resources["SelectedChoiceStyle"];
            presets.Children.Add(button);
        }
        var heading = Ui.Row(title, presets); heading.ColumnSpacing = 4;
        var slider = brightnessSlider = new Slider { Minimum = 10, Maximum = 100, StepFrequency = 1, Value = screenBrightness ?? 100,
            IsEnabled = displayState?.Active == true && screenBrightness.HasValue && !displayBusy, MinHeight = 28 };
        AttachBrightnessEvents(slider);
        var content = Ui.Stack(heading, BrightnessTrack(slider)); content.Spacing = 0;
        var card = Ui.Card(content); card.Padding = new Thickness(8, 6, 8, 4); return PanelAnchor("brightness",card);
    }
    private void AttachBrightnessEvents(Slider slider)
    {
        slider.AddHandler(UIElement.PointerPressedEvent,new Microsoft.UI.Xaml.Input.PointerEventHandler((_,_)=>brightnessEditing=true),true);
        slider.AddHandler(UIElement.PointerReleasedEvent,new Microsoft.UI.Xaml.Input.PointerEventHandler(async (_,_)=>await CommitBrightnessSlider(slider)),true);
        slider.PointerCaptureLost += async (_, args) => { if(!args.Pointer.IsInContact)await CommitBrightnessSlider(slider); };
        slider.KeyDown += (_,_)=>brightnessEditing=true;
        slider.KeyUp += async (_,_)=>await CommitBrightnessSlider(slider);
        slider.LostFocus += async (_,_)=>await CommitBrightnessSlider(slider);
        slider.ValueChanged+=async (_,_)=>
        {
            if(syncingBrightness||!ReferenceEquals(slider,brightnessSlider))return;
            await SetCompactBrightness((int)slider.Value);
        };
    }
    private async Task CommitBrightnessSlider(Slider slider)
    {
        if(!brightnessEditing||!ReferenceEquals(slider,brightnessSlider))return;
        int target=(int)slider.Value;brightnessEditing=false;
        await SetCompactBrightness(target);
    }
    private Task SetCompactBrightness(int value)
    {
        if(value is <10 or >100)throw new ArgumentOutOfRangeException(nameof(value));
        if(displayBusy||displayState?.Active!=true||closed)return Task.CompletedTask;
        requestedBrightness=value;UpdateBrightnessValues();
        if(brightnessWork is {IsCompleted:false})return brightnessWork;
        return brightnessWork=ApplyRequestedBrightness();
    }
    private async Task ApplyRequestedBrightness()
    {
        brightnessApplying=true;
        try
        {
            while(!closed&&requestedBrightness is int target)
            {
                try
                {
                    if(target!=screenBrightness)await writeBrightness(target);
                    screenBrightness=target;
                    if(requestedBrightness==target){requestedBrightness=null;ConfirmedActionOsd("屏幕亮度 · "+target+"%","brightness");}
                }
                catch(Exception error)
                {
                    OpenRevo.Core.DiagnosticLog.Failure("brightness.apply",error,new{target});
                    try{screenBrightness=await Task.Run(OpenRevo.Core.ScreenBrightness.Read);}catch{screenBrightness=null;}
                    // A newer request remains eligible even if the old one failed.
                    if(requestedBrightness!=target)continue;
                    requestedBrightness=null;
                    if(!closed)await ShowMessage("亮度未确认",error.Message);
                    break;
                }
                if(!closed)UpdateBrightnessValues();
            }
        }
        finally{brightnessApplying=false;if(!closed)UpdateBrightnessValues();}
    }
    private void UpdateBrightnessValues()
    {
        int? shown=brightnessEditing&&brightnessSlider!=null?(int)brightnessSlider.Value:requestedBrightness??screenBrightness;
        Put("brightnessTitle",$"☼  屏幕亮度：{shown?.ToString()??"—"}%");
        Put("brightnessDetailTitle",$"内置屏亮度：{shown?.ToString()??"—"}%");
        bool available=displayState?.Active==true&&shown.HasValue&&!displayBusy;
        if(approvedBrightnessNumber!=null){approvedBrightnessNumber.IsEnabled=available;if(!brightnessEditing&&approvedBrightnessNumber.Value!=(shown??double.NaN)){syncingBrightness=true;approvedBrightnessNumber.Value=shown??double.NaN;syncingBrightness=false;}}
        if(brightnessSlider!=null)
        {
            if(brightnessSlider.IsEnabled!=available)brightnessSlider.IsEnabled=available;
            if(!brightnessEditing&&shown is int value&&brightnessSlider.Value!=value)
            {
                syncingBrightness=true;
                try{brightnessSlider.Value=value;}finally{syncingBrightness=false;}
            }
        }
        var selected=(Style)Application.Current.Resources["SelectedChoiceStyle"];
        foreach(var(value,button)in brightnessButtons)
        {
            if(button.IsEnabled!=available)button.IsEnabled=available;
            if(shown==value){if(button.Style!=selected)button.Style=selected;}
            else if(button.Style==selected)button.ClearValue(FrameworkElement.StyleProperty);
        }
    }
}


