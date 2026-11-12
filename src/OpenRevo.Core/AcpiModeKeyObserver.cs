using System.Runtime.InteropServices;

namespace OpenRevo.Core;

// Passive event wait, matching the pinned Core listener. No upstream engine,
// scripting callback host, hardware sampling, or companion process is started.
public sealed class AcpiModeKeyObserver:IDisposable
{
    private readonly Action<int> pressed;
    private volatile bool disposed, installed;
    public bool Installed => installed && !disposed;
    public string? Failure {get;private set;}
    public AcpiModeKeyObserver(Action<int> pressed) : this(pressed, useNativeModeKeyEvents: false) { }

    internal AcpiModeKeyObserver(Action<int> pressed, bool useNativeModeKeyEvents)
    {
        this.pressed=pressed;
        var thread=new Thread(useNativeModeKeyEvents ? ListenNative : Listen){IsBackground=true,Name="OpenRevo mode key"};
        thread.SetApartmentState(ApartmentState.MTA);thread.Start();
    }

    private void ListenNative()
    {
        try
        {
            if (disposed) return;
            int result = ListenModeKeys(ObserveNative);
            if (result < 0)
                throw new COMException($"原生 WMI 实体键订阅失败 (0x{result:X8})。", result);
        }
        catch (Exception error) { ReportFailure(error); }
        finally { installed = false; }
    }

    private int ObserveNative(uint code)
    {
        if (disposed) return 0;
        try
        {
            if (code == 1) MarkInstalled("native-wmi");
            else if (code is 176 or 192 or 201) pressed((int)code);
            return disposed ? 0 : 1;
        }
        catch (Exception error)
        {
            ReportFailure(error);
            return 0;
        }
    }

    private void MarkInstalled(string backend)
    {
        installed = true;
        DiagnosticLog.Record("modekey.subscription.started",
            new { namespaceName = @"root\WMI", eventClass = "AcpiTest_EventULong", backend });
    }

    private void ReportFailure(Exception error)
    {
        if (disposed) return;
        Failure = error.Message;
        DiagnosticLog.Failure("modekey.subscription.failed", error);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ModeKeyCallback(uint code);

    [DllImport("openrevo_native_bridge.dll", EntryPoint = "or_native_listen_mode_keys", ExactSpelling = true,
        CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern int ListenModeKeys(ModeKeyCallback observe);

    private void Listen()
    {
        object? locator=null,services=null,source=null;
        try
        {
            if(disposed)return;
            locator=Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator",true)!);
            services=((dynamic)locator!).ConnectServer(".",@"root\WMI");
            source=((dynamic)services).ExecNotificationQuery("SELECT * FROM AcpiTest_EventULong WHERE ULong=176 OR ULong=192 OR ULong=201","WQL",48);
            MarkInstalled("scripting-wmi");
            while(!disposed)
            {
                object? row=null,properties=null,property=null;
                try
                {
                    // This waits for an event. The timeout only bounds shutdown;
                    // it does not read EC state or repeatedly create subscriptions.
                    row=((dynamic)source).NextEvent(1000);
                    if(disposed)break;
                    properties=((dynamic)row!).Properties_;property=((dynamic)properties).Item("ULong");
                    int code=Convert.ToInt32(((dynamic)property).Value);
                    if(code is 176 or 192 or 201)pressed(code);
                }
                catch(COMException error)when(error.HResult==unchecked((int)0x80043001)){ }
                finally{Release(property);Release(properties);Release(row);}
            }
        }
        catch(Exception error)
        {
            ReportFailure(error);
        }
        finally{installed=false;Release(source);Release(services);Release(locator);}
    }
    // Do not block the UI thread. The owning MTA releases its COM objects after
    // the pending event wait, normally within one second of disconnect/exit.
    public void Dispose(){disposed=true;}
    private static void Release(object? value)
    {
        if(value!=null&&Marshal.IsComObject(value))try{Marshal.ReleaseComObject(value);}catch{ }
    }
}
