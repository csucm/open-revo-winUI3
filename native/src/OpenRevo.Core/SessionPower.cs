using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenRevo.Core;

public interface ISessionPowerRequest : IDisposable { void Set(uint type,bool enabled); }
public sealed class SessionPower : IDisposable
{
    private readonly ISessionPowerRequest request;
    private bool system,display,disposed;
    public bool Enabled=>system;
    public bool DisplayHeld=>display;
    public SessionPower() : this(new NativeRequest()) { }
    public SessionPower(ISessionPowerRequest request)=>this.request=request;
    public void SetKeepAwake(bool enabled)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(enabled)
        {
            bool added=!system;
            if(added){request.Set(1,true);system=true;}
            try {if(!display){request.Set(0,true);display=true;}}
            catch {if(added){request.Set(1,false);system=false;}throw;}
        }
        else
        {
            if(display){request.Set(0,false);display=false;}
            if(system){request.Set(1,false);system=false;}
        }
    }
    public void ReleaseDisplay()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(display){request.Set(0,false);display=false;}
    }
    public void HoldSystemOnly()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(!system){request.Set(1,true);system=true;}
        ReleaseDisplay();
    }
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size,Tick; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput input);
    public static uint ReadLastInputTick()
    {
        var input=new LastInput{Size=(uint)Marshal.SizeOf<LastInput>()};
        if(!GetLastInputInfo(ref input))throw new IOException("无法确认输入状态，请稍后重试。");
        return input.Tick;
    }
    public void Dispose()
    {
        if(disposed)return;
        try {SetKeepAwake(false);}finally{disposed=true;request.Dispose();system=false;display=false;}
    }
    // Target only our window. No broadcast, input blocking, sleep or shutdown.
    public static void TurnOffScreen(nint ownWindow)
    {
        if(ownWindow==0)throw new ArgumentException("窗口不可用。");
        DefWindowProcW(ownWindow,0x112,0xF170,2);
    }
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint window,uint message,nuint wParam,nint lParam);
    private sealed class NativeRequest : ISessionPowerRequest
    {
        [StructLayout(LayoutKind.Explicit,Size=32)] private struct Reason
        {
            [FieldOffset(0)] public uint Version;
            [FieldOffset(4)] public uint Flags;
            [FieldOffset(8)] public nint Text;
        }
        [DllImport("kernel32.dll",SetLastError=true)] private static extern SafeFileHandle PowerCreateRequest(in Reason reason);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool PowerSetRequest(SafeFileHandle handle,uint type);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool PowerClearRequest(SafeFileHandle handle,uint type);
        private readonly SafeFileHandle handle;
        public NativeRequest()
        {
            nint text=Marshal.StringToHGlobalUni("Open-Revo 防休眠");
            try {handle=PowerCreateRequest(new Reason{Version=0,Flags=1,Text=text});}
            finally{Marshal.FreeHGlobal(text);}
            if(handle.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Set(uint type,bool enabled)
        {
            if(type>1)throw new ArgumentOutOfRangeException(nameof(type));
            if(!(enabled?PowerSetRequest(handle,type):PowerClearRequest(handle,type)))throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Dispose()=>handle.Dispose();
    }
}
