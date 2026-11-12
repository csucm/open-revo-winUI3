using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace OpenRevo.Core;

internal static class InstalledLaunchTasks
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private const string OwnerFile = "launch-owner.sid";
    private const string Bootstrap = "OpenRevo.TaskEntry.exe";
    private static string Sid
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User!.Value;
        }
    }

    internal static bool HasInstallation(string directory) => File.Exists(Path.Combine(directory, OwnerFile));

    internal static string RequireResidentBootstrap(string directory)
    {
        var entry = new FileInfo(Path.Combine(directory, Bootstrap));
        ProtectedInstallDirectory.Check(entry);
        if (entry.Length is <= 0 or > 4 * 1024 * 1024)
            throw new IOException("独立后台的任务入口大小无效。");
        string image = Encoding.Unicode.GetString(File.ReadAllBytes(entry.FullName));
        bool native = image.Contains("RevoCore.exe", StringComparison.Ordinal);
        bool managed = image.Contains("OpenRevo.Resident.exe", StringComparison.Ordinal);
        if (native == managed ||
            !image.Contains("--installed-open", StringComparison.Ordinal) ||
            !image.Contains("--installed-logon", StringComparison.Ordinal) ||
            image.Contains("OpenRevo.Native.exe", StringComparison.Ordinal))
            throw new IOException("当前计划任务入口未配对到独立后台。");
        return native ? "RevoCore.exe" : "OpenRevo.Resident.exe";
    }

    internal static void RequireOwner(string directory, bool allowNew = false)
    {
        string marker = Path.Combine(directory, OwnerFile);
        if (allowNew && !File.Exists(marker)) return;
        if (!File.Exists(marker) || File.ReadAllText(marker).Trim() != Sid)
            throw new UnauthorizedAccessException("请用登记此安装的 Windows 登录账户操作。");
    }

    internal static void Install(string directory)
    {
        RequireAdministrator();
        RequireOwner(directory, allowNew: true);
        string marker = Path.Combine(directory, OwnerFile);
        if (!File.Exists(marker))
        {
            using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write);
            using var writer = new StreamWriter(stream);
            writer.Write(Sid);
        }
        SetTask(directory, logon: false, enabled: true);
    }

    internal static void Remove(string directory)
    {
        RequireAdministrator();
        RequireOwner(directory);
        SetTask(directory, logon: false, enabled: false);
        SetTask(directory, logon: true, enabled: false);
        File.Delete(Path.Combine(directory, OwnerFile));
    }

    internal static void SetLogon(string directory, bool enabled) => SetTask(directory, logon: true, enabled);

    internal static bool IsLogonEnabled(string directory) => WithTask(directory, logon: true,
        (task, _, _, _) => task != null && (bool)((dynamic)task).Enabled);

    internal static void ValidateLogon(string directory)
    {
        WithTask(directory, logon: true, (_, folder, name, sid) =>
        {
            // TASK_VALIDATE_ONLY
            Release(((dynamic)folder).RegisterTask(name, BuildXml(directory, sid, logon: true), 1, sid, null, 3, null));
            return true;
        });
    }

    internal static void Open(string directory)
    {
        RequireOwner(directory);
        WithTask(directory, logon: false, (task, _, _, _) =>
        {
            if (task == null || !(bool)((dynamic)task).Enabled)
                throw new IOException("启动授权缺失，请重新运行安装器修复。");
            using var process = Process.GetCurrentProcess();
            // TASK_RUN_USE_SESSION_ID keeps the UI in the requesting interactive session.
            object? instance = ((dynamic)task).RunEx(null, 4, process.SessionId, null);
            if (instance == null) throw new IOException("计划任务未接受启动请求。");
            Release(instance);
            return true;
        });
    }

    private static void SetTask(string directory, bool logon, bool enabled)
    {
        RequireAdministrator();
        RequireOwner(directory);
        WithTask(directory, logon, (previous, folder, name, sid) =>
        {
            if (enabled)
            {
                // GRGX allows the filtered user token to run and inspect this fixed action.
                string acl = $"O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;{sid})";
                // TASK_CREATE_OR_UPDATE | TASK_DONT_ADD_PRINCIPAL_ACE
                Release(((dynamic)folder).RegisterTask(name, BuildXml(directory, sid, logon), 0x16, sid, null, 3, acl));
            }
            else if (previous != null)
            {
                ((dynamic)folder).DeleteTask(name, 0);
            }
            object? actual = ReadTask(folder, name);
            try
            {
                bool confirmed = enabled
                    ? actual != null && (bool)((dynamic)actual).Enabled && Matches((string)((dynamic)actual).Xml, directory, sid, logon)
                        && HasLaunchOnlyAccess((string)((dynamic)actual).GetSecurityDescriptor(5), sid)
                    : actual == null;
                if (!confirmed) throw new IOException("启动任务回读与安装设置不一致。");
            }
            finally { Release(actual); }
            return true;
        });
    }

    private static T WithTask<T>(string directory, bool logon, Func<object?, object, string, string, T> action)
    {
        string sid = Sid;
        string name = $"OpenRevoNative-{(logon ? "Logon" : "Open")}-{sid}";
        object? service = null, folder = null, task = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder(@"\");
            task = ReadTask(folder!, name);
            if (task != null && !Matches((string)((dynamic)task).Xml, directory, sid, logon))
                throw new IOException("启动任务与此安装的账户、路径或动作不符。请检查旧安装。");
            return action(task, folder!, name, sid);
        }
        finally { Release(task); Release(folder); Release(service); }
    }

    private static object? ReadTask(object folder, string name)
    {
        try { return ((dynamic)folder).GetTask(name); }
        catch (COMException error) when (error.HResult == unchecked((int)0x80070002)) { return null; }
        catch (FileNotFoundException) { return null; }
    }

    private static string Arguments(bool logon) => logon ? "--scheduled-logon" : "--scheduled-open";

    private static bool HasLaunchOnlyAccess(string sddl, string sid)
    {
        var security = new RawSecurityDescriptor(sddl);
        const int genericExecute = 0x20000000, fileExecute = 0x20;
        const int genericWriteOrAll = 0x50000000;
        int forbidden = genericWriteOrAll | (int)(FileSystemRights.Write | FileSystemRights.Delete |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | FileSystemRights.DeleteSubdirectoriesAndFiles);
        if (security.Owner?.Value != "S-1-5-32-544" || security.DiscretionaryAcl == null) return false;
        bool canRun = false;
        foreach (var entry in security.DiscretionaryAcl)
        {
            if (entry is not CommonAce ace || ace.AceQualifier != AceQualifier.AccessAllowed) return false;
            string trustee = ace.SecurityIdentifier.Value;
            if (trustee is "S-1-5-18" or "S-1-5-32-544") continue;
            if (trustee != sid || (ace.AccessMask & forbidden) != 0) return false;
            canRun |= (ace.AccessMask & (genericExecute | fileExecute)) != 0;
        }
        return canRun;
    }

    private static string BuildXml(string directory, string sid, bool logon)
    {
        XElement E(string name, params object[] content) => new(Ns + name, content);
        var task = E("Task", new XAttribute("version", "1.2"));
        if (logon) task.Add(E("Triggers", E("LogonTrigger", E("Enabled", "true"), E("UserId", sid))));
        task.Add(
            E("Principals", E("Principal", new XAttribute("id", "Console"), E("UserId", sid),
                E("LogonType", "InteractiveToken"), E("RunLevel", "HighestAvailable"))),
            E("Settings", E("MultipleInstancesPolicy", "Parallel"), E("DisallowStartIfOnBatteries", "false"),
                E("StopIfGoingOnBatteries", "false"), E("AllowStartOnDemand", logon ? "false" : "true"), E("Enabled", "true")),
            E("Actions", new XAttribute("Context", "Console"), E("Exec",
                E("Command", Path.Combine(directory, Bootstrap)), E("Arguments", Arguments(logon)), E("WorkingDirectory", directory))));
        return task.ToString();
    }

    private static bool Matches(string xml, string directory, string sid, bool logon)
    {
        var root = XElement.Parse(xml);
        var actions = root.Element(Ns + "Actions");
        var principals = root.Element(Ns + "Principals")?.Elements().ToArray() ?? [];
        var triggers = root.Element(Ns + "Triggers")?.Elements().ToArray() ?? [];
        bool Identity(string? value)
        {
            if (string.Equals(value, sid, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.IsNullOrWhiteSpace(value)) return false;
            try { return new NTAccount(value).Translate(typeof(SecurityIdentifier)).Value == sid; }
            catch (IdentityNotMappedException) { return false; }
        }
        return root.Name == Ns + "Task" && principals is [var principal] && principal.Name == Ns + "Principal" &&
            Identity((string?)principal.Element(Ns + "UserId")) &&
            (string?)principal.Element(Ns + "LogonType") == "InteractiveToken" &&
            (string?)principal.Element(Ns + "RunLevel") == "HighestAvailable" &&
            actions?.Elements().ToArray() is [var exec] && exec.Name == Ns + "Exec" &&
            (string?)actions.Attribute("Context") == (string?)principal.Attribute("id") &&
            string.Equals((string?)exec.Element(Ns + "Command"), Path.Combine(directory, Bootstrap), StringComparison.OrdinalIgnoreCase) &&
            string.Equals((string?)exec.Element(Ns + "WorkingDirectory"), directory, StringComparison.OrdinalIgnoreCase) &&
            (string?)exec.Element(Ns + "Arguments") == Arguments(logon) &&
            (logon ? triggers is [var trigger] && trigger.Name == Ns + "LogonTrigger" &&
                Identity((string?)trigger.Element(Ns + "UserId")) &&
                (string?)trigger.Element(Ns + "Enabled") is null or "true" or "1" : triggers.Length == 0);
    }

    internal static void RequireAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("此操作需要由管理员安装器执行。");
    }

    private static void Release(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }
}
