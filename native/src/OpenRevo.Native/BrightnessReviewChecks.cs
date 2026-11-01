using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private async Task RunBrightnessInteractionChecks(Action<string,bool> check)
    {
        int? before=screenBrightness;var writer=writeBrightness;
        var writes=new List<int>();var release=new TaskCompletionSource<bool>();
        var callbacks=new List<(DependencyObject Control,long Token)>();int enabledChanges=0;
        try
        {
            if(brightnessSlider==null||displayState?.Active!=true)throw new IOException("Brightness interaction checks require the active internal panel.");
            screenBrightness=100;requestedBrightness=null;UpdateBrightnessValues();
            foreach(var control in brightnessButtons.Values.Cast<Control>().Append(brightnessSlider))
                callbacks.Add((control,control.RegisterPropertyChangedCallback(Control.IsEnabledProperty,(_,_)=>enabledChanges++)));
            writeBrightness=async value=>{writes.Add(value);if(writes.Count==1)await release.Task;};
            brightnessEditing=true;brightnessSlider.Value=37;UpdateDisplayValues();
            check("Drag starts a write before release and retains its value during periodic refresh",brightnessSlider.Value==37&&writes.SequenceEqual(new[]{37}));
            var drag=CommitBrightnessSlider(brightnessSlider);
            var duplicateRelease=CommitBrightnessSlider(brightnessSlider);
            var preset=SetCompactBrightness(60);
            check("Dragging commit accepts a preset while the previous write is pending",writes.SequenceEqual(new[]{37})&&requestedBrightness==60&&brightnessSlider.Value==60&&brightnessButtons.Values.All(b=>b.IsEnabled));
            release.SetResult(true);await Task.WhenAll(drag,preset,duplicateRelease);
            check("Release and focus loss cannot double-submit the drag",writes.SequenceEqual(new[]{37,60})&&screenBrightness==60);
            writes.Clear();release=new TaskCompletionSource<bool>();
            var dark=SetCompactBrightness(25);var indoor=SetCompactBrightness(60);
            check("25 to 60 retains the latest preset even when 60 was the prior confirmed value",requestedBrightness==60&&brightnessSlider.Value==60&&brightnessButtons[60].IsEnabled);
            release.SetResult(true);await Task.WhenAll(dark,indoor);
            check("Queued 25 to 60 finishes at 60 with no enabled-state flicker",writes.SequenceEqual(new[]{25,60})&&screenBrightness==60&&enabledChanges==0);
        }
        finally
        {
            release.TrySetResult(true);if(brightnessWork!=null)await brightnessWork;
            foreach(var(control,token)in callbacks)control.UnregisterPropertyChangedCallback(Control.IsEnabledProperty,token);
            writeBrightness=writer;screenBrightness=before;requestedBrightness=null;brightnessEditing=false;UpdateBrightnessValues();
        }
    }
    private async Task RunLiveBrightnessChecks(Action<string,bool> check)
    {
        if(displayState?.Active!=true||!screenBrightness.HasValue)throw new IOException("Physical BOE panel is not available; live brightness test was not run.");
        int before=await Task.Run(OpenRevo.Core.ScreenBrightness.Read);
        try
        {
            var dark=SetCompactBrightness(25);var indoor=SetCompactBrightness(60);
            await Task.WhenAll(dark,indoor);
            int actual=await Task.Run(OpenRevo.Core.ScreenBrightness.Read);
            check("Real BOE brightness accepts 25 then 60 during an active write",actual==60&&screenBrightness==60&&brightnessSlider?.Value==60&&brightnessButtons[60].IsEnabled);
        }
        finally{await SetCompactBrightness(before);}
        check("Live brightness test restores the user's original brightness",await Task.Run(OpenRevo.Core.ScreenBrightness.Read)==before);
    }
}
