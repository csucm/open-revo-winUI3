using OpenRevo.Core;
using OpenRevo.Core.Hal;

namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private readonly List<object> gpuUiTimings=[];
    private async Task RunLiveGpuChecks(Action<string,bool> check)
    {
        if(!GpuModeControl.IsAdministrator()||upstream==null||!DisplayControl.Read().Active)throw new IOException("GPU review requires an elevated console and active internal panel.");
        var baseline=await Task.Run(upstream.ReadGpuMode);
        if(!baseline.Known||baseline.Mode!=3||gpuBoot?.InitialMode!=3||GpuRestartPending)throw new IOException("GPU review requires the verified unchanged dGPU boot baseline.");
        var displayBefore=DisplayControl.Read();
        try
        {
            bool startupBefore=StartupRegistration.IsEnabled();
            StartupRegistration.ValidateConfiguration();
            check("Highest privilege logon task validates in Windows without changing startup",StartupRegistration.IsEnabled()==startupBefore);
        }
        catch(Exception error){actions.Add(error.ToString());check("Highest privilege logon task validates in Windows: "+error.Message,false);}
        check("Elevated GPU read uses the current process token",(await GpuModeControl.Request(0)).Success);
        check("Prior 3→2→3 selection no longer shows restart pending",!GpuRestartPending&&requestedGpu==0);
        try
        {
            foreach(int target in new[]{2,3})
            {
                var watch=System.Diagnostics.Stopwatch.StartNew();
                await RunGpuRequest(target);
                var actual=await Task.Run(upstream.ReadGpuMode);
                gpuUiTimings.Add(new{target,actual,elapsedMs=watch.ElapsedMilliseconds,pending=GpuRestartPending});
                check($"GPU {target} firmware saved and UI restart requirement matches boot baseline",actual.Mode==target&&gpuState?.Mode==target&&GpuRestartPending==(target!=3));
                var reloaded=GpuBootStateStore.Open(actual.Mode);
                check($"GPU {target} restart status survives reloading saved session",reloaded.InitialMode==3&&reloaded.Pending==(target!=3));
                if(actual.Mode!=target)throw new IOException("GPU stage did not confirm; stopping test.");
            }
        }
        finally
        {
            var actual=await Task.Run(upstream.ReadGpuMode);
            if(actual.Known&&actual.Mode!=baseline.Mode)
            {
                gpuState=actual;await RunGpuRequest(baseline.Mode);
            }
            var final=await Task.Run(upstream.ReadGpuMode);
            check("GPU safe test restored firmware and cleared pending flag",final.Known&&final.Mode==baseline.Mode&&!GpuRestartPending);
            // Compare rates explicitly because DisplayState contains an array.
            var displayAfter=DisplayControl.Read();
            check("Physical display mode is unchanged during firmware-only roundtrip",displayAfter.DeviceName==displayBefore.DeviceName&&displayAfter.Width==displayBefore.Width&&displayAfter.Height==displayBefore.Height&&displayAfter.Hertz==displayBefore.Hertz);
        }
    }
}
