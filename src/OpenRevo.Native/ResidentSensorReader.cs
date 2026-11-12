using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenRevo.Core;
using OpenRevo.ResidentProtocol;

namespace OpenRevo.Native;

internal readonly record struct ResidentSensorFields(bool CpuMetrics, bool CpuThermal, bool GpuThermal,
    bool GpuMetrics, bool GpuMemory, bool GpuTarget)
{
    internal bool Any => CpuMetrics || CpuThermal || GpuThermal || GpuMetrics || GpuMemory || GpuTarget;
}

internal sealed record ResidentSensorCapabilities(bool Supported, bool ConfigurableBase,
    int FloorWatts, int BaseMaxWatts, int BoostMaxWatts);
internal sealed record ResidentSensorReading(DateTimeOffset At, PanelOverviewCpu? Cpu, int? CpuTemperature,
    int? GpuTemperature, PanelOverviewGpu? Gpu, ResidentSensorCapabilities? Capabilities);

/// <summary>Owns read-only counters and driver connections on one foreground worker thread.</summary>
internal sealed class ResidentSensorReader : IDisposable
{
    private readonly BlockingCollection<(PanelSensorPlan Plan, ResidentSensorFields Fields, Action<ResidentSensorReading> Deliver)> pending = new(1);
    private volatile bool stopping;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint Open();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Read(nint state, byte[] request,
        nuint size, [Out] byte[] buffer, nuint capacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Close(nint state);

    internal ResidentSensorReader()
    {
        new Thread(Run) { IsBackground = true, Name = "OpenRevo panel sensors" }.Start();
    }

    internal bool TryQueue(PanelSensorPlan plan, ResidentSensorFields fields, Action<ResidentSensorReading> deliver) =>
        !stopping && pending.TryAdd((plan, fields, deliver));

    private void Run()
    {
        nint library = 0;
        nint state = 0;
        Close? close = null;
        try
        {
            var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "openrevo_native_bridge.dll"));
            library = NativeLibrary.Load(path);
            close = Marshal.GetDelegateForFunctionPointer<Close>(NativeLibrary.GetExport(library, "or_native_panel_sensor_close"));
            var open = Marshal.GetDelegateForFunctionPointer<Open>(NativeLibrary.GetExport(library, "or_native_panel_sensor_open"));
            var read = Marshal.GetDelegateForFunctionPointer<Read>(NativeLibrary.GetExport(library, "or_native_panel_sensor_read"));
            state = open();
            if (state == 0) throw new IOException("无法打开面板传感器。");
            byte[] buffer = new byte[16 * 1024];
            foreach (var (plan, fields, deliver) in pending.GetConsumingEnumerable())
            {
                if (stopping) break;
                byte[] request = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    fields.CpuMetrics, fields.CpuThermal, fields.GpuThermal, fields.GpuMetrics, fields.GpuMemory,
                    plan.SampleGpu, plan.AllowGpu, plan.Discrete, plan.CoreCpu, plan.Generation,
                    ProbeCaps = plan.ProbeCaps && fields.GpuTarget
                });
                try
                {
                    int size = read(state, request, (nuint)request.Length, buffer, (nuint)buffer.Length);
                    if (size > 0 && size <= buffer.Length && !stopping &&
                        JsonSerializer.Deserialize<ResidentSensorReading>(buffer.AsSpan(0, size)) is { } reading)
                        deliver(reading);
                }
                catch (Exception error) { DiagnosticLog.Failure("resident.panel.sensor_read", error, throttle: true); }
            }
        }
        catch (Exception error) { DiagnosticLog.Failure("resident.panel.sensor_open", error, throttle: true); }
        finally
        {
            if (state != 0) close?.Invoke(state);
            if (library != 0) NativeLibrary.Free(library);
        }
    }

    public void Dispose()
    {
        stopping = true;
        pending.CompleteAdding();
    }
}
