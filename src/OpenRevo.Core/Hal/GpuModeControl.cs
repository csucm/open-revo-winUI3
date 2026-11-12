using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace OpenRevo.Core.Hal;

public sealed record GpuModeReply(bool Success,GpuModeState? State,bool Changed,string? Error);
public static class GpuModeControl
{
    private const string Prefix="OpenRevo.Gpu.";
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint processId);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint processId);
    // Target 0 reads only; 1..3 stage a user-selected next-boot mode; 4 verifies
    // the native same-mode/no-write path. The pipe never accepts commands.
    public static async Task<GpuModeReply> Request(int target,int expected=0,string? helperExe=null)
    {
        if(target is <0 or >4 || expected is <0 or >3)throw new ArgumentOutOfRangeException(nameof(target));
        string? blocked=target is 0 or 4?HardwareSession.CheckMachineIdentity():HardwareSession.CheckEnvironment();
        if(blocked!=null)throw new InvalidOperationException(blocked);
        // The console now starts elevated, like upstream. Reuse that token rather
        // than starting another RunAs process for every mode selection.
        if(IsAdministrator())return await Task.Run(()=>Execute(target,expected));
        string nonce=Guid.NewGuid().ToString("N");
        using var identity=WindowsIdentity.GetCurrent();
        var security=new PipeSecurity();security.SetAccessRuleProtection(true,false);security.SetOwner(identity.User!);
        security.AddAccessRule(new PipeAccessRule(identity.User!,PipeAccessRights.FullControl,AccessControlType.Allow));
        using var server=NamedPipeServerStreamAcl.Create(Prefix+nonce,PipeDirection.In,1,PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous|PipeOptions.FirstPipeInstance,4096,4096,security);
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var connected=server.WaitForConnectionAsync(timeout.Token);
        string executable=helperExe??Environment.ProcessPath!;
        if(!Path.IsPathFullyQualified(executable)||Path.GetFileName(executable)!="OpenRevo.Native.exe")throw new ArgumentException("显卡助手路径无效。");
        var start=new ProcessStartInfo(executable){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden};
        foreach(string value in new[]{"--gpu-helper",nonce,Environment.ProcessId.ToString(),target.ToString(),expected.ToString()})start.ArgumentList.Add(value);
        using var process=await Task.Run(()=>Process.Start(start))??throw new IOException("显卡设置未启动。");
        var exited=process.WaitForExitAsync(timeout.Token);
        await Task.WhenAny(connected,exited);
        if(exited.IsCompleted&&!connected.IsCompletedSuccessfully)throw new IOException("显卡助手未能连接，请重新读取。");
        await connected;
        if(!GetNamedPipeClientProcessId(server.SafePipeHandle,out uint client) || client!=process.Id)throw new IOException("显卡助手身份未确认。");
        using var content=new MemoryStream();byte[] buffer=new byte[2048];
        while(true)
        {
            int count=await server.ReadAsync(buffer,timeout.Token);if(count==0)break;
            if(content.Length+count>16384)throw new IOException("显卡助手返回内容异常。");
            content.Write(buffer,0,count);
        }
        await exited;
        var reply=JsonSerializer.Deserialize<GpuModeReply>(content.ToArray())??throw new IOException("显卡助手没有返回状态。");
        if(process.ExitCode!=0 && reply.Success)throw new IOException("显卡结果未确认，请重新读取。");
        if(reply.Success&&reply.State?.Known==true)try{GpuModeCache.Save(reply.State);}catch{}
        return reply;
    }
    public static int RunPrivileged(string nonce,int parentPid,int target,int expected)
    {
        if(!Guid.TryParseExact(nonce,"N",out _) || parentPid<=0 || target is <0 or >4 || expected is <0 or >3)return 10;
        using var identity=WindowsIdentity.GetCurrent();
        if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))return 12;
        try
        {
            using var parent=Process.GetProcessById(parentPid);
            if(parent.HasExited || parent.SessionId!=Process.GetCurrentProcess().SessionId)return 12;
            using var pipe=new NamedPipeClientStream(".",Prefix+nonce,PipeDirection.Out,PipeOptions.Asynchronous);
            pipe.Connect(10000);
            if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle,out uint serverPid)||serverPid!=parentPid)return 12;
            var reply=Execute(target,expected);
            byte[] json=JsonSerializer.SerializeToUtf8Bytes(reply);pipe.Write(json);pipe.Flush();
            return reply.Success?0:11;
        }
        catch{return 12;}
    }
    public static bool IsAdministrator()
    {
        using var identity=WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
    private static GpuModeReply Execute(int target,int expected)
    {
            UpstreamCoreLibrary? core=null;GpuModeState? state=null;GpuModeReply reply;
            try
            {
                var identityError=HardwareSession.CheckMachineIdentity();if(identityError!=null)throw new InvalidOperationException(identityError);
                core=new UpstreamCoreLibrary(Path.Combine(AppContext.BaseDirectory,"openrevo_native_bridge.dll"));
                state=core.ReadGpuMode();if(!state.Known)throw new IOException("无法读取固件中的显卡设置。");
                bool changed=false;
                if(target is >=1 and <=3)
                {
                    var session=HardwareSession.ForThisMachine();session.Takeover();
                    var result=session.ChangeGpuMode(core,target,expected);state=result.After;changed=result.Changed;
                }
                else if(target==4 && core.StageGpuMode(state.Mode,state.Mode))throw new IOException("同模式校验意外执行了写入。");
                reply=new(true,state,changed,null);
            }
            catch(Exception error)
            {
                try{if(core!=null)state=core.ReadGpuMode();}catch{}
                try{Directory.CreateDirectory(AppData.DiagnosticsPath);File.WriteAllText(Path.Combine(AppData.DiagnosticsPath,"gpu-helper-error.txt"),error.ToString());}catch{}
                reply=new(false,state,false,error is InvalidOperationException?error.Message:"显卡结果未确认，请重新读取设置。");
            }
            finally{core?.Dispose();}
            if(reply.Success&&reply.State?.Known==true)try{GpuModeCache.Save(reply.State);}catch{}
            return reply;
    }
}
public sealed partial class HardwareSession
{
    public GpuModeReceipt ChangeGpuMode(IGpuModeCore core,int target,int expectedMode=0)=>Serialized(()=>
    {
        if(target is <1 or >3 || expectedMode is <0 or >3)throw new ArgumentOutOfRangeException(nameof(target));
        if(!Active)throw new InvalidOperationException("请先连接硬件。");
        Guard();using var ec=open();if(!MatchesProject(Read(ec).Project))throw new IOException("设备不匹配。");
        var before=core.ReadGpuMode();if(!before.Known)throw new InvalidOperationException("无法读取当前显卡设置。");
        if(expectedMode!=0 && before.Mode!=expectedMode)throw new InvalidOperationException("显卡设置已变化，已刷新状态，请再次选择。");
        if(before.Mode==target)return new GpuModeReceipt(before,before,false);
        if(!before.Supports(target))throw new InvalidOperationException("本机不支持所选显卡模式。");
        audit(new{at=DateTimeOffset.Now,operation="gpu-next-boot-intent",before,target});
        try
        {
            Guard();bool changed=core.StageGpuMode(before.Mode,target);var after=core.ReadGpuMode();
            if(!changed || !after.Known || after.Mode!=target)throw new IOException("显卡设置回读不一致。");
            var receipt=new GpuModeReceipt(before,after,true);
            audit(new{at=DateTimeOffset.Now,operation="gpu-next-boot-confirmed",receipt});return receipt;
        }
        catch{faulted=true;throw;}
    });
}
