using System.Runtime.InteropServices;
using OpenRevo.Core;
namespace OpenRevo.Native;

internal sealed class CapsLockObserver:IDisposable
{
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready=new();
    private readonly CapsLockTransitions state;
    private readonly Action<bool> changed;
    private readonly Hook callback;
    private volatile bool disposed;
    private nint handle;
    private uint threadId;
    internal bool Installed{get;private set;}
    internal string? Failure{get;private set;}
    internal static bool ReadState()=>(GetKeyState(0x14)&1)!=0;
    internal CapsLockObserver(Action<bool> onChanged)
    {
        state=new(ReadState());changed=onChanged;callback=OnKey;
        thread=new Thread(Run){IsBackground=true,Name="OpenRevo Caps notification"};thread.Start();
        if(!ready.Wait(1000)){Failure="Caps监听初始化超时。";Dispose();}
    }
    private void Run()
    {
        try
        {
            threadId=GetCurrentThreadId();PeekMessage(out _,0,0,0,0);
            handle=SetWindowsHookEx(13,callback,GetModuleHandle(null),0);
            if(handle==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            Installed=true;ready.Set();
            while(!disposed&&GetMessage(out _,0,0,0)>0){}
        }
        catch(Exception error){Failure=error.Message;}
        finally{if(handle!=0)UnhookWindowsHookEx(handle);handle=0;Installed=false;ready.Set();}
    }
    private nint OnKey(int code,nuint message,nint data)
    {
        if(code>=0&&!disposed&&Marshal.ReadInt32(data)==0x14)
        {
            bool down=message is 0x100 or 0x104,up=message is 0x101 or 0x105;
            if(down||up)
            {
                var value=state.Accept(0x14,down);
                if(value.HasValue)try{changed(value.Value);}catch{ /* Never block Windows keyboard delivery. */ }
            }
        }
        return CallNextHookEx(handle,code,message,data);
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        if(threadId!=0)PostThreadMessage(threadId,0x12,0,0);
        if(Thread.CurrentThread!=thread)thread.Join(500);
    }
    [StructLayout(LayoutKind.Sequential)]private struct Message{public nint Window;public uint Id;public nuint Wparam;public nint Lparam;public uint Time;public int X,Y;public uint Private;}
    private delegate nint Hook(int code,nuint message,nint data);
    [DllImport("user32.dll",EntryPoint="SetWindowsHookExW",SetLastError=true)]private static extern nint SetWindowsHookEx(int id,Hook callback,nint module,uint thread);
    [DllImport("user32.dll")]private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]private static extern nint CallNextHookEx(nint hook,int code,nuint message,nint data);
    [DllImport("user32.dll",EntryPoint="PeekMessageW")]private static extern bool PeekMessage(out Message message,nint window,uint min,uint max,uint flags);
    [DllImport("user32.dll",EntryPoint="GetMessageW")]private static extern int GetMessage(out Message message,nint window,uint min,uint max);
    [DllImport("user32.dll",EntryPoint="PostThreadMessageW")]private static extern bool PostThreadMessage(uint thread,uint message,nuint wp,nint lp);
    [DllImport("user32.dll")]private static extern short GetKeyState(int key);
    [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll",EntryPoint="GetModuleHandleW",CharSet=CharSet.Unicode)]private static extern nint GetModuleHandle(string? module);
}

public sealed partial class MainWindow
{
    private CapsLockObserver? capsLockObserver;
    private void StartCapsLockMonitor()
    {
        if(uiReviewMode||capsLockObserver!=null)return;
        capsLockObserver=new(locked=>DispatcherQueue.TryEnqueue(()=>
        {
            if(closed||exitRequested||!osdEnabled)return;
            PresentOsd(OsdMessage.FromFeature("caps_lock",locked?"1":"0"));
        }));
        if(!capsLockObserver.Installed)actions.Add("Caps提示未启动："+capsLockObserver.Failure);
    }
}
