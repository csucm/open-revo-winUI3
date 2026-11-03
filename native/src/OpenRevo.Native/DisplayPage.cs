using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private DisplayState? displayState;
    private int? screenBrightness;
    private bool displayBusy;
    private async Task ReadDisplay()
    {
        try { displayState = await Task.Run(DisplayControl.Read);DiagnosticLog.Record("display.state",displayState); } catch(Exception error) { displayState = null;DiagnosticLog.Failure("display.read",error,new{topology=DisplayTopology.LastSnapshot},true); }
        try { screenBrightness = await Task.Run(ScreenBrightness.Read);DiagnosticLog.Record("brightness.state",new{value=screenBrightness}); } catch(Exception error) { screenBrightness = null;DiagnosticLog.Failure("brightness.read",error,throttle:true); }
    }
    private async Task ChangeRefresh(int target)
    {
        if (dialogOpen || displayBusy || brightnessApplying || displayState?.Active != true || displayState.Hertz == target) return;
        displayBusy = true;
        UpdateDisplayValues();
        int original = displayState.Hertz; bool keep = false, changed = false;
        try
        {
            await Task.Run(() => DisplayControl.SetRefresh(original, target)); changed = true;
            if(UsePaintedUi)keep=await PaintDialog($"保留 {target} Hz？",Paint.Text("15 秒内确认，否则自动恢复原刷新率。"),"保留","恢复",15000);
            else
            {
            var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = $"保留 {target} Hz？", Content = Ui.Text("15 秒内确认，否则自动恢复原刷新率。"), PrimaryButtonText = "保留", CloseButtonText = "恢复", DefaultButton = ContentDialogButton.Close };
            dialogOpen = true;
            var shown = dialog.ShowAsync().AsTask();
            if (await Task.WhenAny(shown, Task.Delay(15000)) != shown) dialog.Hide();
            keep = await shown == ContentDialogResult.Primary;
            }
        }
        catch (Exception e) { DiagnosticLog.Failure("display.refresh",e,new{original,target});actions.Add("刷新率操作：" + e.Message); }
        finally
        {
            dialogOpen = false;
            if (changed && !keep)
            {
                try { await Task.Run(() => DisplayControl.SetRefresh(target, original)); }
                catch (Exception e) { await ShowMessage("刷新率恢复未确认", e.Message); }
            }
            await ReadDisplay(); displayBusy = false; if (!closed) UpdateDisplayValues();if(changed&&displayState?.Hertz==(keep?target:original))ConfirmedActionOsd((keep?"屏幕刷新率 · ":"已恢复刷新率 · ")+displayState.Hertz+" Hz","refresh");
        }
    }
}
