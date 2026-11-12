using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace OpenRevo.Core;

public sealed record PowerPolicyDefinition(string Key,Guid Group,Guid Setting,uint On,uint Off,bool DcOnly=false);
public sealed record PowerPolicyValue(PowerPolicyDefinition Definition,uint? Ac,uint? Dc)
{
    public bool Supported=>Dc.HasValue&&(Definition.DcOnly||Ac.HasValue);
    public bool? Enabled=>!Supported?null:Definition.DcOnly?Dc==Definition.On:Ac==Dc?Dc==Definition.On:null;
}
public static class DesktopPower
{
    public static readonly PowerPolicyDefinition[] Policies=[
        new("disconnect_net",new("fea3413e-7e05-4911-9a71-700331f1c294"),new("f15576e8-98b7-4186-b944-eafa664402d9"),0,1),
        new("block_away",new("238c9fa8-0aad-41ed-83f4-97be242c8f20"),new("25dfa149-5dd1-4736-b5ab-e8a37b5b8187"),0,1),
        new("ignore_sysreq",new("238c9fa8-0aad-41ed-83f4-97be242c8f20"),new("a4b195f5-8225-47d8-8012-9d41369786e2"),0,1),
        new("disable_wake_timers",new("238c9fa8-0aad-41ed-83f4-97be242c8f20"),new("bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d"),0,1),
        new("deep_sleep",new("2e601130-5351-4d9d-8e04-252966bad054"),new("d502f7ee-1dc7-4efd-a55d-f04b6f5c0545"),1,0),
        new("hibernate_30m",new("238c9fa8-0aad-41ed-83f4-97be242c8f20"),new("9d7815a6-7ee4-497e-8888-515a05f02364"),1800,0,true)
    ];
    public static PowerPolicyValue[] ReadPolicies()
    {
        Guid scheme=new WindowsPowerPlans().Active();
        return Policies.Select(p=>new PowerPolicyValue(p,Read(scheme,p,false),Read(scheme,p,true))).ToArray();
    }
    private static uint? Read(Guid scheme,PowerPolicyDefinition p,bool dc)
    {
        Guid group=p.Group,setting=p.Setting;
        uint code=dc?PowerReadDCValueIndex(0,ref scheme,ref group,ref setting,out uint result):PowerReadACValueIndex(0,ref scheme,ref group,ref setting,out result);
        return code==0?result:null;
    }
    public static void SetPolicy(UpstreamCoreLibrary core,string key,bool enabled)
    {
        var plans=new WindowsPowerPlans();Guid scheme=plans.Active();
        var p=ReadPolicies().Single(p=>p.Definition.Key==key);
        if(!p.Supported)throw new NotSupportedException("Windows未提供此睡眠策略。");
        core.ExecuteFeature(1,new{key,enabled,scheme=scheme.ToString()});
        if(plans.Active()!=scheme)throw new IOException("电源方案在设置期间发生变化，请重读。未切回旧方案。");
        plans.Set(scheme);
        var after=ReadPolicies().Single(p=>p.Definition.Key==key);
        if(after.Enabled!=enabled)throw new IOException("睡眠策略回读不一致，请重新读取。已停止后续操作。");
    }
    public static Guid Overlay()
    {
        uint error=PowerGetEffectiveOverlayScheme(out Guid scheme);if(error!=0)throw new Win32Exception((int)error);
        return scheme;
    }
    public static void SetOverlay(UpstreamCoreLibrary core,int mode)
    {
        Guid expected=mode switch{1=>new("961cc777-2547-4f9d-8174-7d86181b8a7a"),2 or 4=>Guid.Empty,3=>new("ded574b5-45a0-4f42-8737-46345c09c238"),_=>throw new ArgumentOutOfRangeException(nameof(mode))};
        core.ExecuteFeature(3,new{mode});
        if(Overlay()!=expected)throw new IOException("Windows性能策略回读未确认。");
    }
    [DllImport("powrprof.dll")]private static extern uint PowerReadACValueIndex(nint root,ref Guid scheme,ref Guid group,ref Guid setting,out uint value);
    [DllImport("powrprof.dll")]private static extern uint PowerReadDCValueIndex(nint root,ref Guid scheme,ref Guid group,ref Guid setting,out uint value);
    [DllImport("powrprof.dll")]private static extern uint PowerGetEffectiveOverlayScheme(out Guid scheme);
}
public sealed class DesktopServicePreferences
{
    public bool SleepOffice{get;set;}
    public bool PerformanceLink{get;set;}
    private static string FilePath=>Path.Combine(AppData.DirectoryPath,"desktop-services.json");
    public static DesktopServicePreferences Load(){try{return JsonSerializer.Deserialize<DesktopServicePreferences>(File.ReadAllText(FilePath))??new();}catch(IOException){return new();}catch(JsonException){return new();}catch(UnauthorizedAccessException){return new();}}
    public void Save(){Directory.CreateDirectory(AppData.DirectoryPath);string temp=FilePath+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(this));File.Move(temp,FilePath,true);}
}
public interface IDesktopPowerCore {void SetDesktopPowerOption(string key,byte expected,bool enabled);}
public sealed partial class UpstreamCoreLibrary:IDesktopPowerCore
{
    public void SetDesktopPowerOption(string key,byte expected,bool enabled)=>ExecuteFeature(2,new{key,expected,enabled});
    public void StopDesktopServices()
    {
        DiagnosticLog.Record("core.services.shutdown.waiting");
        lock(gate) lock(winLockGate)
        {
            ObjectDisposedException.ThrowIf(library==0,this);
            if(NativeLibrary.TryGetExport(library,"or_native_services_shutdown",out var cleanup))
            {
                DiagnosticLog.Record("core.services.shutdown.entered");
                var watch=System.Diagnostics.Stopwatch.StartNew();
                int result=Marshal.GetDelegateForFunctionPointer<CameraAccess>(cleanup)();
                DiagnosticLog.Record("core.services.shutdown.result",new{result,elapsedMs=watch.ElapsedMilliseconds});
                if(result!=0)throw new IOException($"桌面服务或 Win 软件锁未能释放（{result}），请重试退出。");
            }
        }
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int FeatureSet(uint action,nint data,nuint length);
    public void ExecuteFeature(uint action,object request)
    {
        if(action is <1 or >13)throw new ArgumentOutOfRangeException(nameof(action));
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(request);
        if(bytes.Length>24*1024*1024)throw new ArgumentOutOfRangeException(nameof(request));
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(library==0,this);
            nint memory=Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes,0,memory,bytes.Length);
                var call=Marshal.GetDelegateForFunctionPointer<FeatureSet>(NativeLibrary.GetExport(library,"or_native_feature_set"));
                if(call(action,memory,(nuint)bytes.Length)!=0)throw new IOException(ReadLocked(17,0).GetString()??"Core服务未完成。");
            }
            finally{Marshal.FreeHGlobal(memory);}
        }
    }
}
