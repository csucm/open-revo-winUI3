using Microsoft.Win32;
using System.Xml.Linq;

namespace OpenRevo.Core;

public static partial class StartupRegistration
{
    public static void MigrateLegacy()
    {
        using var key=Registry.CurrentUser.OpenSubKey(KeyPath);
        // Preserve an existing choice; never opt in a new user or migrate a
        // different install/preview's registry entry.
        if(MatchesLegacyCommand(key?.GetValue(ValueName) as string,Executable()))SetEnabled(true);
    }
    private static string Executable()
    {
        string path=Environment.ProcessPath??throw new IOException("无法确定应用路径。");
        if(!Path.IsPathFullyQualified(path)||Path.GetFileName(path)!="OpenRevo.Native.exe"||path.Contains('"'))throw new IOException("应用路径无效。");
        return path;
    }
    public static string BuildTaskXml(string executable,string sid)
    {
        if(!Path.IsPathFullyQualified(executable)||Path.GetFileName(executable)!="OpenRevo.Native.exe"||!sid.StartsWith("S-1-",StringComparison.Ordinal))throw new ArgumentException("自启任务身份或路径无效。");
        XNamespace ns="http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name,object? value)=>new(ns+name,value);
        return new XElement(ns+"Task",new XAttribute("version","1.2"),
            E("RegistrationInfo",E("Description","Open-Revo 控制台登录时启动")),
            E("Triggers",E("LogonTrigger",new[]{E("Enabled","true"),E("UserId",sid)})),
            E("Principals",new XElement(ns+"Principal",new XAttribute("id","Console"),E("UserId",sid),E("LogonType","InteractiveToken"),E("RunLevel","HighestAvailable"))),
            E("Settings",new[]{E("MultipleInstancesPolicy","IgnoreNew"),E("DisallowStartIfOnBatteries","false"),E("StopIfGoingOnBatteries","false"),E("AllowStartOnDemand","true"),E("Enabled","true"),E("ExecutionTimeLimit","PT0S")}),
            new XElement(ns+"Actions",new XAttribute("Context","Console"),E("Exec",new[]{E("Command",executable),E("Arguments","--startup"),E("WorkingDirectory",Path.GetDirectoryName(executable))}))).ToString();
    }
    public static bool MatchesTask(string xml,string executable,string sid)=>InspectTask(xml,executable,sid).Matches;
    public static object? LastInspection{get;private set;}
    public static bool IsEnabled()
    {
        if (InstalledLaunchTasks.HasInstallation(AppContext.BaseDirectory))
        {
            bool enabled = InstalledLaunchTasks.IsLogonEnabled(Path.GetDirectoryName(Executable())!);
            LastInspection = new { at = DateTimeOffset.UtcNow, installedLogon = true, enabled };
            return enabled;
        }
        object? service=null,folder=null;
        try
        {
            service=Connect();folder=((dynamic)service).GetFolder(@"\");
            string? xml=ReadTask((dynamic)folder);
            var inspection=xml==null?null:InspectTask(xml,Executable(),Sid);LastInspection=new{at=DateTimeOffset.UtcNow,exists=xml!=null,inspection};
            return inspection?.Matches==true;
        }
        catch(Exception error){DiagnosticLog.Failure("startup.query",error,throttle:true);throw;}
        finally{Release(folder);Release(service);}
    }

    public static void ValidateResidentInstallation(string directory)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!directory.Equals(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase) ||
            !InstalledLaunchTasks.HasInstallation(directory) || !File.Exists(Path.Combine(directory, "unins000.exe")))
            throw new IOException("当前路径没有有效的 Open-Revo 安装登记。");
        ProtectedInstallDirectory.Prepare(directory, create: false);
        InstalledLaunchTasks.RequireResidentBootstrap(directory);
    }

    public static bool ReadResidentLogon(string directory)
    {
        ValidateResidentInstallation(directory);
        return InstalledLaunchTasks.IsLogonEnabled(directory);
    }

    public static string PrepareResidentUpdateCache(string directory)
    {
        ValidateResidentInstallation(directory);
        var cache = ProtectedInstallDirectory.CheckLocation(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + ".openrevo-updates");
        if (!cache.Exists) ProtectedInstallDirectory.CreateProtected(cache);
        ProtectedInstallDirectory.Check(cache);
        return cache.FullName;
    }

    public static void ValidateResidentUpdatePayload(string directory, string payload)
    {
        string cache = PrepareResidentUpdateCache(directory);
        var file = new FileInfo(payload);
        var staging = file.Directory;
        if (staging?.Parent == null ||
            !cache.Equals(staging.Parent.FullName, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(staging.Name, "N", out _) ||
            !file.Name.Equals("Open-Revo-Setup-x64.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("安装包路径不属于受保护的更新目录。");
        ProtectedInstallDirectory.Check(staging);
        ProtectedInstallDirectory.Check(file);
    }

    public static bool SetResidentLogon(string directory, bool enabled)
    {
        ValidateResidentInstallation(directory);
        InstalledLaunchTasks.SetLogon(directory, enabled);
        return InstalledLaunchTasks.IsLogonEnabled(directory);
    }
    public static void SetEnabled(bool enabled)
    {
        if (InstalledLaunchTasks.HasInstallation(AppContext.BaseDirectory))
        {
            string directory = Path.GetDirectoryName(Executable())!;
            ProtectedInstallDirectory.Prepare(directory, create: false);
            InstalledLaunchTasks.SetLogon(directory, enabled);
            return;
        }
        using var operation=DiagnosticLog.Begin("startup.registration",new{enabled});
        object? service=null,folder=null,registered=null;
        try
        {
            if(!Hal.GpuModeControl.IsAdministrator())throw new IOException("设置开机自启需要管理员权限，请重新启动控制台。");
            string path=Executable();service=Connect();folder=((dynamic)service).GetFolder(@"\");
            string? previous=ReadTask((dynamic)folder);
            var previousInspection=previous==null?null:InspectTask(previous,path,Sid);
            DiagnosticLog.Record("startup.previous",new{path,exists=previous!=null,inspection=previousInspection});
            if(previousInspection!=null&&!previousInspection.Owned)throw new IOException("自启任务属于其他路径、用户或未知动作，未覆盖任务。差异："+string.Join(", ",previousInspection.Differences));
            DiagnosticLog.Record("startup.scheduler.request",new{enabled,action=enabled?"register":"delete",alreadyAbsent=previous==null});
            if(enabled)registered=((dynamic)folder).RegisterTask(TaskName,BuildTaskXml(path,Sid),6,Sid,null,3,null);
            else if(previous!=null)((dynamic)folder).DeleteTask(TaskName,0);
            StartupTaskInspection? actualInspection=null;bool confirmed=false;
            for(int attempt=0;attempt<3;attempt++)
            {
                if(attempt>0)Thread.Sleep(100);
                string? actual=ReadTask((dynamic)folder);actualInspection=actual==null?null:InspectTask(actual,path,Sid);
                LastInspection=new{at=DateTimeOffset.UtcNow,requested=enabled,exists=actual!=null,inspection=actualInspection};
                DiagnosticLog.Record("startup.scheduler.readback",new{attempt=attempt+1,requested=enabled,exists=actual!=null,inspection=actualInspection});
                if(enabled?actualInspection?.Matches==true:actual==null){confirmed=true;break;}
            }
            if(!confirmed)throw new IOException("开机启动设置回读不一致。差异："+(actualInspection==null?"task.missing":string.Join(", ",actualInspection.Differences)));
            // Only migrate our exact legacy command, never another preview/install path.
            using var key=Registry.CurrentUser.OpenSubKey(KeyPath,true);
            if(MatchesLegacyCommand(key?.GetValue(ValueName) as string,path))key!.DeleteValue(ValueName,false);
            operation.Complete(new{enabled,inspection=actualInspection});
        }
        catch(Exception error){operation.Fail(error,new{lastReadback=LastInspection});throw;}
        finally{Release(registered);Release(folder);Release(service);}
    }
    public static void MigrateInstalledLogon(string previousDirectory) =>
        MigrateInstalledLogon(Path.GetDirectoryName(Executable())!, previousDirectory);
    // TASK_VALIDATE_ONLY checks the real scheduler schema without registering
    // a task or altering the user's startup choice.
    public static void ValidateConfiguration()
    {
        if (InstalledLaunchTasks.HasInstallation(AppContext.BaseDirectory))
        {
            InstalledLaunchTasks.ValidateLogon(Path.GetDirectoryName(Executable())!);
            return;
        }
        object? service=null,folder=null,validation=null;
        try
        {
            service=Connect();folder=((dynamic)service).GetFolder(@"\");
            validation=((dynamic)folder).RegisterTask(TaskName,BuildTaskXml(Executable(),Sid),1,Sid,null,3,null);
        }
        finally{Release(validation);Release(folder);Release(service);}
    }
}
