using System.Runtime.InteropServices;

namespace OpenRevo.Core;

// Keep the PDH module alive for the query and its counters.
internal sealed class PerformanceCounterQuery : IDisposable
{
    private nint module, query;
    private readonly AddCounter add;
    private readonly QueryOperation collect, close;
    private readonly ReadArray read;

    private PerformanceCounterQuery(nint module)
    {
        this.module = module;
        add = Load<AddCounter>(module, "PdhAddEnglishCounterW");
        collect = Load<QueryOperation>(module, "PdhCollectQueryData");
        close = Load<QueryOperation>(module, "PdhCloseQuery");
        read = Load<ReadArray>(module, "PdhGetFormattedCounterArrayW");
    }

    internal static uint Open(out PerformanceCounterQuery? result)
    {
        result = null;
        nint module = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "pdh.dll"));
        try
        {
            var open = Load<OpenQuery>(module, "PdhOpenQueryW");
            var instance = new PerformanceCounterQuery(module);
            uint status = open(0, 0, out instance.query);
            if (status == 0)
            {
                result = instance;
                module = 0;
            }
            return status;
        }
        finally { if (module != 0) NativeLibrary.Free(module); }
    }

    internal uint Add(string path, out nint counter) => add(query, path, 0, out counter);
    internal uint Collect() => collect(query);
    internal uint Read(nint counter, uint format, ref uint bytes, out uint count, nint buffer) =>
        read(counter, format, ref bytes, out count, buffer);

    public void Dispose()
    {
        if (module == 0) return;
        try { close(query); }
        finally
        {
            NativeLibrary.Free(module);
            module = query = 0;
        }
    }

    private static T Load<T>(nint module, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(module, name));

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint OpenQuery(nint source, nuint user, out nint query);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    private delegate uint AddCounter(nint query, string path, nuint user, out nint counter);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint QueryOperation(nint query);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint ReadArray(nint counter, uint format, ref uint bytes, out uint count, nint buffer);
}
