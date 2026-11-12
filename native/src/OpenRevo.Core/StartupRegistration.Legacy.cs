using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;

namespace OpenRevo.Core;

public sealed record StartupTaskInspection(bool Owned,bool Matches,bool Enabled,string[] Differences,object? Fields=null);

public static partial class StartupRegistration
{
    private const string KeyPath=@"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName="OpenRevoNative";
    private static string Sid=>WindowsIdentity.GetCurrent().User!.Value;
    private static string TaskName=>"OpenRevoNative-"+Sid;
    public static bool MatchesLegacyCommand(string? command,string executable)=>
        Path.IsPathFullyQualified(executable)&&Path.GetFileName(executable)=="OpenRevo.Native.exe"&&!executable.Contains('"')&&
        string.Equals(command,$"\"{executable}\" --startup",StringComparison.OrdinalIgnoreCase);

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

    internal static void MigrateInstalledLogon(string directory, string previousDirectory)
    {
        ProtectedInstallDirectory.Prepare(directory, create: false);
        string previous = Path.Combine(Path.GetFullPath(previousDirectory), "OpenRevo.Native.exe");
        object? service = null, folder = null;
        try
        {
            service = Connect();
            folder = ((dynamic)service).GetFolder(@"\");
            string? xml = ReadTask((dynamic)folder);
            var task = xml == null ? null : InspectTask(xml, previous, Sid);
            if (task != null && !task.Owned)
                throw new IOException("已有的登录自启任务属于其他安装路径，未修改。");
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            bool legacy = MatchesLegacyCommand(key?.GetValue(ValueName) as string, previous);
            if (task?.Enabled == true || legacy) InstalledLaunchTasks.SetLogon(directory, true);
            if (task != null) ((dynamic)folder).DeleteTask(TaskName, 0);
            if (legacy) key!.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        finally { Release(folder); Release(service); }
    }

    private static void Release(object? value){if(value!=null&&Marshal.IsComObject(value))Marshal.FinalReleaseComObject(value);}
}
