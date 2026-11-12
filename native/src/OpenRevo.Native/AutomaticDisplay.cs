using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private DispatcherQueueTimer? powerTimer;
    private bool lowRefreshOnBattery, powerCheckRunning;
    private readonly Dictionary<int, Button> refreshButtons = [];
    private Button LowRefreshButton()
    {
        Button button = null!;
        button = Ui.Button("拔电低刷", async (_, _) =>
        {
            if(residentClient!=null){await ToggleResidentLowRefreshAsync();buttonStyle(lowRefreshOnBattery);return;}
            lowRefreshOnBattery = !lowRefreshOnBattery;
            if (lowRefreshOnBattery) buttonStyle(true); else buttonStyle(false);
            await SavePreferences(); await CheckAutomaticRefresh(true);
        });
        void buttonStyle(bool selected)
        {
            if (selected) button.Style = (Style)Application.Current.Resources["SelectedChoiceStyle"];
            else button.ClearValue(FrameworkElement.StyleProperty);
        }
        buttonStyle(lowRefreshOnBattery);
        button.FontSize = 11; button.Padding = new Thickness(3, 6, 3, 6);
        ToolTipService.SetToolTip(button, "拔电降至60 Hz省电，接电恢复之前的刷新率");
        return button;
    }
    private async Task CheckAutomaticRefresh(bool force = false, bool refreshAfterWake = false)
    {
        if (closed || !hardware.Active || powerCheckRunning || displayBusy || brightnessApplying || displayTopologyPending || displayTopologyReading) return;
        powerCheckRunning = true;
        try
        {
            int revision=displayTopologyRevision;
            var sample=await hardwareRuntime.Display.EvaluateAutomaticAsync(lowRefreshOnBattery,!trayHidden,force,refreshAfterWake);
            if(sample==null||closed||revision!=displayTopologyRevision)return;
            displayState=sample;
            UpdateDisplayValues();
        }
        catch (Exception error) { actions.Add("自动刷新率：" + error.Message); if(actions.Count>128)actions.RemoveAt(0); }
        finally { powerCheckRunning = false; }
    }
    private void StartPowerPolicy()
    {
        powerTimer = DispatcherQueue.CreateTimer(); powerTimer.Interval = TimeSpan.FromSeconds(3);
        SampleResources();
        powerTimer.Tick += async (_, _) => { SampleResources(); RestoreScreenRequestOnInput(); await CheckAutomaticRefresh(); await RetryStartupTakeover(); await SyncPerformanceOverlay();await EnforceBatteryPerformanceGuard(); await RestoreDeclaredTuningOnAc(); }; powerTimer.Start();
    }
    private void UpdateDisplayValues()
    {
        UpdateMiniRefreshControls();Put("approvedRefreshValue",displayState?.Active==true?displayState.Hertz+" Hz":"—");if(approvedRefreshChoice!=null)approvedRefreshChoice.IsEnabled=displayState?.Active==true&&!displayBusy;
        Put("refreshTitle", displayState?.Active == true ? $"屏幕刷新率：{displayState.Hertz} Hz" : "屏幕刷新率：— Hz");
        foreach (var (rate, button) in refreshButtons)
        {
            button.IsEnabled = displayState?.Active == true && displayState.Rates.Contains(rate) && !displayBusy;
            if (displayState?.Active == true && displayState.Hertz == rate) button.Style = (Style)Application.Current.Resources["SelectedChoiceStyle"];
            else button.ClearValue(FrameworkElement.StyleProperty);
        }
        UpdateBrightnessValues();
    }
}
