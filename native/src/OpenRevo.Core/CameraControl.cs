using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using OpenRevo.Core.Hal;
using Microsoft.Win32;
using System.Security.Principal;

namespace OpenRevo.Core;

public sealed record CameraState(string InstanceId, bool? Enabled);
public static class CameraControl
{
    // Verified integrated HD Webcam on GM6IQ0C. Never select by class alone.
    private const string BuiltinPrefix=@"USB\VID_2B7E&PID_B651&MI_00\";
    private static readonly Guid CameraClass=new("ca3e7ab9-b4c3-4ae6-8251-579ef933890f");
    public static bool MachineAccessBlocked()
    {
        using var webcam=Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam");
        using var desktop=webcam?.OpenSubKey("NonPackaged");
        using var userDesktop=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam\NonPackaged");
        return string.Equals(webcam?.GetValue("Value") as string,"Deny",StringComparison.OrdinalIgnoreCase) ||
            string.Equals(desktop?.GetValue("Value") as string,"Deny",StringComparison.OrdinalIgnoreCase) ||
            string.Equals(userDesktop?.GetValue("Value") as string,"Deny",StringComparison.OrdinalIgnoreCase);
    }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfo { public uint Size; public Guid Class; public uint Instance; public nuint Reserved; }
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern nint SetupDiGetClassDevsW(in Guid guid,string? enumerator,nint parent,uint flags);
    [DllImport("setupapi.dll",SetLastError=true)] private static extern bool SetupDiEnumDeviceInfo(nint set,uint index,ref DeviceInfo info);
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool SetupDiGetDeviceInstanceIdW(nint set,ref DeviceInfo info,StringBuilder id,uint size,out uint required);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(nint set);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_DevNode_Status(out uint status,out uint problem,uint instance,uint flags);
    public static CameraState? Read()
    {
        nint set=SetupDiGetClassDevsW(CameraClass,null,0,0);
        if(set==-1)return null;
        try
        {
            var matches=new List<CameraState>();
            for(uint i=0;;i++)
            {
                var info=new DeviceInfo{Size=(uint)Marshal.SizeOf<DeviceInfo>()};
                if(!SetupDiEnumDeviceInfo(set,i,ref info))
                {
                    if(Marshal.GetLastWin32Error()!=259)return null;
                    break;
                }
                var id=new StringBuilder(1024);
                if(!SetupDiGetDeviceInstanceIdW(set,ref info,id,1024,out _))return null;
                if(!id.ToString().StartsWith(BuiltinPrefix,StringComparison.OrdinalIgnoreCase))continue;
                bool? enabled=null;
                if(CM_Get_DevNode_Status(out uint status,out uint problem,info.Instance,0)==0)
                    enabled=problem==22?false:problem==0&&(status&8)!=0?true:null;
                matches.Add(new(id.ToString(),enabled));
            }
            return matches.Count==1?matches[0]:null;
        }
        finally {SetupDiDestroyDeviceInfoList(set);}
    }
    public static async Task Request(bool enabled)
    {
        string? blocked=HardwareSession.CheckEnvironment();if(blocked!=null)throw new InvalidOperationException(blocked);
        if(Read()==null)throw new IOException("未识别到已适配的笔记本摄像头。");
        var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden};
        start.ArgumentList.Add("--camera-helper");start.ArgumentList.Add(enabled?"1":"0");
        using var process=Process.Start(start)??throw new IOException("相机设置未启动。");
        await process.WaitForExitAsync();
        if(process.ExitCode!=0)throw new IOException("相机设置未完成。请检查管理员授权或相机设备状态。");
        if(Read()?.Enabled!=enabled)throw new IOException("相机状态未确认改变，请检查设备管理器。");
    }
    public static int RunPrivileged(bool enabled)
    {
        try
        {
            if(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))return 12;
            var session=HardwareSession.ForThisMachine();session.Takeover();
            var baseline=Read()??throw new IOException("本机摄像头不匹配。");
            using var core=new UpstreamCoreLibrary(Path.Combine(AppContext.BaseDirectory,"openrevo_native_bridge.dll"));
            if(enabled)session.SetCameraPoweredViaCore(core,true);
            // Privacy recovery must not depend on enabling an already-started
            // device. Only a real PnP transition invokes the device utility.
            if(NeedsDeviceTransition(baseline.Enabled,enabled))
            {
                var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"pnputil.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                start.ArgumentList.Add(enabled?"/enable-device":"/disable-device");start.ArgumentList.Add(baseline.InstanceId);
                using var process=Process.Start(start)??throw new IOException("设备设置未启动。");
                var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
                process.WaitForExit();Task.WaitAll(output,error);
                // No /reboot: retain the exact failure for local diagnostics.
                if(process.ExitCode!=0)throw new IOException($"相机设备设置失败（{process.ExitCode}）。{output.Result} {error.Result}");
            }
            for(int i=0;i<40&&Read()?.Enabled!=enabled;i++)Thread.Sleep(100);
            if(Read()?.Enabled!=enabled)throw new IOException("相机设备状态未确认。");
            if(enabled)core.AllowCameraAccess();
            else session.SetCameraPoweredViaCore(core,false);
            return 0;
        }
        catch(Exception e)
        {
            try{Directory.CreateDirectory(AppData.DiagnosticsPath);File.WriteAllText(Path.Combine(AppData.DiagnosticsPath,"camera-error.txt"),e.ToString());}catch{}
            return 11;
        }
    }
    public static int PreparePrivileged()
    {
        if(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))return 12;
        var session=HardwareSession.ForThisMachine();session.Takeover();
        bool beforePowered=session.ProbeSwitches().CameraPowered;
        using var core=new UpstreamCoreLibrary(Path.Combine(AppContext.BaseDirectory,"openrevo_native_bridge.dll"));
        int result;
        try { result=RunPrivileged(true); }
        finally
        {
            // Recover Windows access once and restore the user's hardware switch.
            session.SetCameraPoweredViaCore(core,beforePowered);
        }
        return result;
    }
    public static bool NeedsDeviceTransition(bool? current,bool target)=>current.HasValue&&current.Value!=target;
}
