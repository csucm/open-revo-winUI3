using System.Runtime.InteropServices;

namespace OpenRevo.Core;

public sealed record TemperatureReading(int? Cpu, int? Gpu, string CpuSource = "Windows 处理器温区");

/// <summary>One bounded sample per request; no background worker or GPU subprocess.</summary>
public sealed class TemperatureSensors : IDisposable
{
    private nint query, counter, nvml;
    private readonly object gate = new();
    private bool disposed;
    private NvInit? init; private NvShutdown? shutdown; private NvHandle? handle; private NvTemp? temperature;
    public TemperatureReading Read(bool sampleGpu)
    {
        lock (gate)
        {
            if (disposed) return new(null, null);
            return new(ReadCpu(), sampleGpu ? ReadGpu() : null);
        }
    }
    private int? ReadCpu()
    {
        if (query == 0)
        {
            if (PdhOpenQueryW(null, 0, out query) != 0) return null;
            if (PdhAddEnglishCounterW(query, @"\Thermal Zone Information(*)\Temperature", 0, out counter) != 0)
            { PdhCloseQuery(query); query = 0; return null; }
        }
        if (PdhCollectQueryData(query) != 0) return null;
        uint bytes = 0, count = 0;
        PdhGetFormattedCounterArrayW(counter, 0x200, ref bytes, out count, 0);
        if (bytes == 0 || bytes > 1024*1024) return null;
        nint buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, 0x200, ref bytes, out count, buffer) != 0) return null;
            int? result = null; int size = Marshal.SizeOf<CounterItem>();
            for (int i = 0; i < count && (i+1)*size <= bytes; i++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(buffer + i*size);
                if (item.Value.Status > 1) continue;
                double celsius = item.Value.Value - 273.15;
                if (celsius is > 0 and < 125) result = Math.Max(result ?? 0, (int)Math.Round(celsius));
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private int? ReadGpu()
    {
        try
        {
            if (nvml == 0)
            {
                string path = Path.Combine(Environment.SystemDirectory, "nvml.dll");
                if (!File.Exists(path)) return null;
                nvml = NativeLibrary.Load(path);
                T Load<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(nvml, name));
                init = Load<NvInit>("nvmlInit_v2"); shutdown = Load<NvShutdown>("nvmlShutdown");
                handle = Load<NvHandle>("nvmlDeviceGetHandleByIndex_v2"); temperature = Load<NvTemp>("nvmlDeviceGetTemperature");
                if (init() != 0) { NativeLibrary.Free(nvml); nvml = 0; return null; }
            }
            if (handle!(0, out var gpu) == 0 && temperature!(gpu, 0, out uint value) == 0 && value is > 0 and < 125) return (int)value;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { if (nvml != 0) { NativeLibrary.Free(nvml); nvml = 0; } }
        return null;
    }
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            if (query != 0) { PdhCloseQuery(query); query = 0; }
            if (nvml != 0) { shutdown?.Invoke(); NativeLibrary.Free(nvml); nvml = 0; }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct CounterValue { public uint Status; public double Value; }
    [StructLayout(LayoutKind.Sequential)] private struct CounterItem { public nint Name; public CounterValue Value; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvInit();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvShutdown();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvHandle(uint index, out nint device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvTemp(nint device, uint sensor, out uint value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? source, nuint user, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(nint query, string path, nuint user, out nint counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bytes, out uint count, nint buffer);
}
