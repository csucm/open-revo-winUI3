using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;

namespace OpenRevo.Core;

public sealed record StartupTaskInspection(bool Owned,bool Matches,bool Enabled,string[] Differences,object? Fields=null);

public static class StartupRegistration
{
    private const string KeyPath=@"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName="OpenRevoNative";
    private static string Sid=>WindowsIdentity.GetCurrent().User!.Value;
    private static string TaskName=>"OpenRevoNative-"+Sid;
    public static bool MatchesLegacyCommand(string? command,string executable)=>
        Path.IsPathFullyQualified(executable)&&Path.GetFileName(executable)=="OpenRevo.Native.exe"&&!executable.Contains('"')&&
        string.Equals(command,$"\"{executable}\" --startup",StringComparison.OrdinalIgnoreCase);
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
    public static StartupTaskInspection InspectTask(string xml,string executable,string sid,Func<string,string?>? resolveIdentity=null)
    {
        var task=XElement.Parse(xml);XNamespace ns=task.Name.Namespace;var differences=new List<string>();
        var principal=task.Element(ns+"Principals")?.Element(ns+"Principal");
        var actions=task.Element(ns+"Actions");var exec=actions?.Element(ns+"Exec");
        var triggers=task.Element(ns+"Triggers");var logon=triggers?.Element(ns+"LogonTrigger");
        string Value(XElement? parent,string name)=>((string?)parent?.Element(ns+name))?.Trim()??"";
        bool Identity(string value)
        {
            if(string.Equals(value,sid,StringComparison.OrdinalIgnoreCase))return true;if(string.IsNullOrWhiteSpace(value))return false;
            try{return string.Equals(resolveIdentity!=null?resolveIdentity(value):new NTAccount(value).Translate(typeof(SecurityIdentifier)).Value,sid,StringComparison.OrdinalIgnoreCase);}catch(IdentityNotMappedException){return false;}catch(ArgumentException){return false;}
        }
        bool PathMatch(string value){try{return Path.IsPathFullyQualified(value.Trim('"'))&&string.Equals(Path.GetFullPath(value.Trim('"')),Path.GetFullPath(executable),StringComparison.OrdinalIgnoreCase);}catch(ArgumentException){return false;}}
        bool Enabled(XElement? parent){string value=Value(parent,"Enabled");return value.Length==0||value is "true" or "1";}
        void Require(bool ok,string field){if(!ok)differences.Add(field);}
        bool singleAction=actions?.Elements().Count()==1&&exec!=null;
        bool singleTrigger=triggers?.Elements().Count()==1&&logon!=null;
        bool principalMatch=task.Element(ns+"Principals")?.Elements().Count()==1&&Identity(Value(principal,"UserId"));
        bool pathMatch=PathMatch(Value(exec,"Command")),arguments=Value(exec,"Arguments")=="--startup";
        bool owned=task.Name.LocalName=="Task"&&ns.NamespaceName=="http://schemas.microsoft.com/windows/2004/02/mit/task"&&singleAction&&singleTrigger&&principalMatch&&pathMatch&&arguments;
        Require(task.Name.LocalName=="Task"&&ns.NamespaceName=="http://schemas.microsoft.com/windows/2004/02/mit/task","task.schema");
        Require(singleAction,"actions.singleExec");Require(singleTrigger,"triggers.singleLogon");Require(principalMatch,"principal.currentUser");Require(pathMatch,"exec.path");Require(arguments,"exec.arguments");
        Require(Value(principal,"RunLevel")=="HighestAvailable","principal.runLevel");Require(Value(principal,"LogonType")=="InteractiveToken","principal.logonType");Require(Identity(Value(logon,"UserId")),"trigger.currentUser");
        string context=(string?)actions?.Attribute("Context")??"",principalId=(string?)principal?.Attribute("id")??"";
        Require(context.Length==0||string.Equals(context,principalId,StringComparison.Ordinal),"actions.principalContext");
        bool enabled=Enabled(logon)&&Enabled(task.Element(ns+"Settings"));Require(Enabled(logon),"trigger.enabled");Require(Enabled(task.Element(ns+"Settings")),"settings.enabled");
        string IdentityFormat(string value)=>value.Length==0?"missing":value.StartsWith("S-1-",StringComparison.OrdinalIgnoreCase)?"sid":"account-name";
        return new(owned,differences.Count==0,enabled,differences.ToArray(),new{actualRunLevel=Value(principal,"RunLevel"),actualLogonType=Value(principal,"LogonType"),taskEnabledXml=Value(task.Element(ns+"Settings"),"Enabled"),triggerEnabledXml=Value(logon,"Enabled"),principalIdentityFormat=IdentityFormat(Value(principal,"UserId")),triggerIdentityFormat=IdentityFormat(Value(logon,"UserId")),actionCount=actions?.Elements().Count(),triggerCount=triggers?.Elements().Count(),pathMatches=pathMatch,argumentsMatch=arguments});
    }
    public static bool MatchesTask(string xml,string executable,string sid)=>InspectTask(xml,executable,sid).Matches;
    public static object? LastInspection{get;private set;}
    private static dynamic Connect()
    {
        dynamic service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        try{service.Connect();return service;}catch{Marshal.FinalReleaseComObject(service);throw;}
    }
    private static string? ReadTask(dynamic folder)
    {
        object? task=null;
        try{task=folder.GetTask(TaskName);return ((dynamic)task).Xml;}
        catch(COMException error)when(error.HResult==unchecked((int)0x80070002)){return null;}
        catch(FileNotFoundException){return null;}
        finally{Release(task);}
    }
    public static bool IsEnabled()
    {
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
    public static void SetEnabled(bool enabled)
    {
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
    // TASK_VALIDATE_ONLY checks the real scheduler schema without registering
    // a task or altering the user's startup choice.
    public static void ValidateConfiguration()
    {
        object? service=null,folder=null,validation=null;
        try
        {
            service=Connect();folder=((dynamic)service).GetFolder(@"\");
            validation=((dynamic)folder).RegisterTask(TaskName,BuildTaskXml(Executable(),Sid),1,Sid,null,3,null);
        }
        finally{Release(validation);Release(folder);Release(service);}
    }
    private static void Release(object? value){if(value!=null&&Marshal.IsComObject(value))Marshal.FinalReleaseComObject(value);}
}
