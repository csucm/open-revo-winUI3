using Microsoft.UI.Dispatching;
using OpenRevo.Core;

namespace OpenRevo.Native;

public sealed partial class MainWindow
{
    private DispatcherQueueTimer? displayTopologyDebounce;
    private int displayTopologyRevision;
    private bool displayTopologyPending,displayTopologyQueued,displayTopologyReading;
    private DisplayState? displayBeforeTopologyRead;

    private void QueueDisplayTopologyRead()
    {
        if(closed||exitRequested||uiReviewMode)return;
        if(displayState!=null)displayBeforeTopologyRead=displayState;
        displayTopologyRevision++;displayTopologyPending=true;
        if(displayTopologyQueued)return;
        displayTopologyQueued=true;
        if(!DispatcherQueue.TryEnqueue(()=>
        {
            displayTopologyQueued=false;
            if(closed||exitRequested)return;
            // A read or pending slider request from the old screen must not survive a switch.
            displayState=null;screenBrightness=null;requestedBrightness=null;brightnessEditing=false;
            UpdateDisplayValues();RefreshPaintedState();
            ScheduleDisplayTopologyRead();
        }))displayTopologyQueued=false;
    }

    private void ScheduleDisplayTopologyRead()
    {
        if(closed||exitRequested||!displayTopologyPending)return;
        if(displayTopologyDebounce==null)
        {
            displayTopologyDebounce=DispatcherQueue.CreateTimer();
            displayTopologyDebounce.IsRepeating=false;
            displayTopologyDebounce.Interval=TimeSpan.FromMilliseconds(350);
            displayTopologyDebounce.Tick+=OnDisplayTopologyReadTick;
        }
        // Merge the display/device notification burst for this switch, with no idle timer.
        displayTopologyDebounce.Stop();displayTopologyDebounce.Start();
    }

    private async void OnDisplayTopologyReadTick(DispatcherQueueTimer sender,object args)
    {
        if(closed||exitRequested||!displayTopologyPending)return;
        // Wait for startup and existing operations; never overlap reads or interrupt a write.
        if(powerTimer==null||displayTopologyReading||powerCheckRunning||displayBusy||brightnessApplying)
        {ScheduleDisplayTopologyRead();return;}
        int revision=displayTopologyRevision;
        displayTopologyPending=false;displayTopologyReading=true;
        using var trace=DiagnosticLog.Begin("display.topology_refresh",new{revision});
        try
        {
            var sample=await hardwareRuntime.Display.ReadAsync();
            if(closed||exitRequested){trace.Complete(outcome:"closed");return;}
            if(revision!=displayTopologyRevision){trace.Complete(outcome:"superseded");return;}
            displayState=sample.Panel;screenBrightness=sample.Brightness;
            // Preserve the existing low-refresh restore cache on same-panel mode changes.
            if(displayState is {} current&&displayBeforeTopologyRead is {} previous
                &&(current.DeviceName!=previous.DeviceName||current.Active!=previous.Active))
                hardwareRuntime.Display.InvalidateTopology();
            DiagnosticLog.Record("brightness.state",new{value=screenBrightness,reason="display-switch",revision});
            trace.Complete(new{display=displayState,brightness=screenBrightness},outcome:screenBrightness.HasValue?"success":"unavailable");
            UpdateDisplayValues();RefreshPaintedState();
        }
        catch(Exception error){trace.Fail(error);}
        finally
        {
            displayTopologyReading=false;
            if(displayTopologyPending)ScheduleDisplayTopologyRead();
        }
    }

    private void DisposeDisplayTopologyRead()
    {
        displayTopologyPending=false;
        if(displayTopologyDebounce==null)return;
        displayTopologyDebounce.Stop();displayTopologyDebounce.Tick-=OnDisplayTopologyReadTick;
        displayTopologyDebounce=null;
    }
}
