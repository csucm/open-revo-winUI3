using System.Runtime.InteropServices;

namespace OpenRevo.Core;

// Passive event wait, matching the pinned Core listener. No upstream engine,
// scripting callback host, hardware sampling, or companion process is started.
public sealed class AcpiModeKeyObserver:IDisposable
{
    private readonly Action<int> pressed;
    private volatile bool disposed;
    public bool Installed {get;private set;}
    public string? Failure {get;private set;}
    public AcpiModeKeyObserver(Action<int> pressed)
    {
        this.pressed=pressed;
        var thread=new Thread(Listen){IsBackground=true,Name="OpenRevo mode key"};
        thread.SetApartmentState(ApartmentState.MTA);thread.Start();
    }
    private void Listen()
    {
        object? locator=null,services=null,source=null;
        try
        {
            if(disposed)return;
            locator=Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator",true)!);
            services=((dynamic)locator!).ConnectServer(".",@"root\WMI");
            source=((dynamic)services).ExecNotificationQuery("SELECT * FROM AcpiTest_EventULong WHERE ULong=176 OR ULong=192 OR ULong=201","WQL",48);
            Installed=true;
            DiagnosticLog.Record("modekey.subscription.started",new{namespaceName=@"root\WMI",eventClass="AcpiTest_EventULong"});
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
            if(!disposed){Failure=error.Message;DiagnosticLog.Failure("modekey.subscription.failed",error);}
        }
        finally{Installed=false;Release(source);Release(services);Release(locator);}
    }
    // Do not block the UI thread. The owning MTA releases its COM objects after
    // the pending event wait, normally within one second of disconnect/exit.
    public void Dispose(){disposed=true;Installed=false;}
    private static void Release(object? value)
    {
        if(value!=null&&Marshal.IsComObject(value))try{Marshal.ReleaseComObject(value);}catch{ }
    }
}
