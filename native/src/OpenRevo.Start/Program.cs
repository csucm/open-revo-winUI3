using System.Runtime.InteropServices;
using System.Security.Principal;
using OpenRevo.Core;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                InstalledLaunchTasks.Open(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
                return 0;
            }

            InstalledLaunchTasks.RequireAdministrator();
            RequireInteractiveUser();
            if (args is not [var operation, var path] || !Path.IsPathFullyQualified(path))
                throw new ArgumentException("安装维护参数无效。");
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            switch (operation)
            {
                case "--prepare":
                    if (NeedsLegacyUpgrade(path)) LegacyUpgrade.Validate(path);
                    else ProtectedInstallDirectory.Prepare(path, create: true, preflight: true);
                    break;
                case "--begin-upgrade":
                    if (NeedsLegacyUpgrade(path)) LegacyUpgrade.Begin(path);
                    break;
                case "--require-stopped":
                    ResidentTransition.RequireStopped();
                    break;
                case "--register":
                    ProtectedInstallDirectory.Prepare(path, create: true);
                    if (!InstalledLaunchTasks.HasInstallation(path)) ProtectedInstallDirectory.EnableDataFolder(path, "plugins");
                    LegacyUpgrade.CopyUserData(path);
                    InstalledLaunchTasks.Install(path);
                    break;
                case "--validate-resident-payload":
                    ProtectedInstallDirectory.Prepare(path, create: true);
                    ValidateResidentPayload(path);
                    break;
                case "--retire-legacy-console":
                    ProtectedInstallDirectory.Prepare(path, create: false);
                    ValidateResidentPayload(path);
                    ResidentTransition.RetireLegacyConsole(path);
                    break;
                case "--commit-upgrade":
                    LegacyUpgrade.Commit(path);
                    break;
                case "--migrate-legacy-logon":
                    StartupRegistration.MigrateInstalledLogon(
                        Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), path);
                    break;
                case "--restore-upgrade":
                    LegacyUpgrade.Restore(path);
                    break;
                case "--check-uninstall":
                    ProtectedInstallDirectory.Prepare(path, create: false);
                    break;
                case "--remove":
                    ProtectedInstallDirectory.Prepare(path, create: false);
                    InstalledLaunchTasks.Remove(path);
                    break;
                default:
                    throw new ArgumentException("未知的安装维护操作。");
            }
            return 0;
        }
        catch (Exception error)
        {
            MessageBox(0, error.Message, "Open-Revo", 0x10);
            return 1;
        }
    }

    private static void ValidateResidentPayload(string path)
    {
        string host = InstalledLaunchTasks.RequireResidentBootstrap(path);
        string panel = host == "RevoCore.exe" ? "Open Revo" : "OpenRevo.ResidentNativePanel";
        foreach (string file in new[]
        {
            host,
            "OpenRevo.TaskEntry.exe", "OpenRevo.ResidentProtocol.dll", "openrevo_native_bridge.dll",
            "PluginWorker/OpenRevo.PluginWorker.exe", "PluginWorker/OpenRevo.PluginWorker.runtimeconfig.json",
            $"panel-native/{panel}.exe", $"panel-native/{panel}.dll", $"panel-native/{panel}.pri",
            $"panel-native/{panel}.deps.json", $"panel-native/{panel}.runtimeconfig.json", "panel-native/App.xbf",
            "panel-native/OpenRevo.ResidentProtocol.dll"
        })
            ProtectedInstallDirectory.Check(new FileInfo(Path.Combine(path, file)));
        using(var stream=File.OpenRead(Path.Combine(path,host)))
        using(var image=new System.Reflection.PortableExecutable.PEReader(stream))
        {
            if(image.PEHeaders.CorHeader==null)
            {
                if(image.PEHeaders.CoffHeader.Machine!=System.Reflection.PortableExecutable.Machine.Amd64||image.PEHeaders.PEHeader?.Magic!=System.Reflection.PortableExecutable.PEMagic.PE32Plus)
                    throw new IOException("原生Core必须是x64程序。");
                foreach(string file in new[]{"OpenRevo.Tools.exe","OpenRevo.Tools.dll","OpenRevo.Tools.deps.json","OpenRevo.Tools.runtimeconfig.json","OpenRevo.Core.dll"})
                    ProtectedInstallDirectory.Check(new FileInfo(Path.Combine(path,file)));
                foreach(string retired in new[]{"OpenRevo.Resident.dll","OpenRevo.Resident.deps.json","OpenRevo.Resident.runtimeconfig.json"})
                    if(File.Exists(Path.Combine(path,retired)))throw new IOException("请移除上一版托管后台文件后重试安装。");
                var version=System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(path,host));
                if(version.ProductVersion!="0.7.0"||version.FileVersion!="1.0.6")throw new IOException("原生Core版本未配对。");
            }
            else foreach(string file in new[]{"OpenRevo.Resident.dll","OpenRevo.Resident.runtimeconfig.json"})
                ProtectedInstallDirectory.Check(new FileInfo(Path.Combine(path,file)));
        }

    }

    private static bool NeedsLegacyUpgrade(string path) =>
        LegacyUpgrade.IsPending(path) ||
        (LegacyUpgrade.IsRegistered(path) && !InstalledLaunchTasks.HasInstallation(path));

    private static void RequireInteractiveUser()
    {
        string user = ReadSessionString(5);
        string domain = ReadSessionString(7);
        using var identity = WindowsIdentity.GetCurrent();
        if (user.Length == 0 || domain.Length == 0 ||
            new NTAccount(domain, user).Translate(typeof(SecurityIdentifier)) != identity.User)
            throw new UnauthorizedAccessException("请从当前具有管理员资格的 Windows 登录账户运行安装程序。");
    }

    private static string ReadSessionString(int information)
    {
        if (!WTSQuerySessionInformation(0, -1, information, out nint buffer, out _))
            throw new IOException("无法识别当前 Windows 交互会话。");
        try { return Marshal.PtrToStringUni(buffer) ?? ""; }
        finally { WTSFreeMemory(buffer); }
    }

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW")]
    private static extern bool WTSQuerySessionInformation(nint server, int session, int information, out nint buffer, out int bytes);
    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint memory);
    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint parent, string text, string caption, uint type);
}
