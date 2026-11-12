using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using OpenRevo.Core;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

internal static class LegacyUpgrade
{
    private const string Registration = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{8626505B-AD8A-45AA-B1EB-8AB75B60524A}_is1";
    private const string DataMarker = "openrevo-native-data.json";

    private static string Backup(string path) => path + ".openrevo-previous";
    private static string Previous(string path) => Path.Combine(Backup(path), "installation");
    private static string BackupIdentity(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!.Value + "\n" + path;
    }

    internal static bool IsRegistered(string path)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var registry = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            using var key = registry.OpenSubKey(Registration);
            if (key?.GetValue("Inno Setup: App Path") is string registered &&
                key.GetValue("UninstallString") is string uninstaller &&
                Path.IsPathFullyQualified(registered) &&
                string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(registered)), path, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(uninstaller.Trim('"'), Path.Combine(path, "unins000.exe"), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    internal static bool IsPending(string path)
    {
        if (!IsRegistered(path) || !Directory.Exists(Backup(path))) return false;
        return CheckBackup(path, initialize: false) && Directory.Exists(Previous(path)) && !IsComplete(path);
    }

    internal static void Validate(string path)
    {
        ProtectedInstallDirectory.CheckLocation(path);
        if (IsPending(path))
        {
            CheckLegacyRoot(Previous(path));
            return;
        }
        CheckLegacyRoot(path);
    }

    internal static void Begin(string path)
    {
        Validate(path);
        if (IsPending(path)) Restore(path);
        var backup = new DirectoryInfo(Backup(path));
        if (!backup.Exists) ProtectedInstallDirectory.CreateProtected(backup);
        CheckBackup(path, initialize: true);
        if (IsComplete(path) || Directory.Exists(Previous(path)))
            throw new IOException("已有旧版备份，未覆盖：" + backup.FullName);

        Directory.Move(path, Previous(path));
        ProtectedInstallDirectory.Prepare(path, create: true, preflight: true);
    }

    private static bool CheckBackup(string path, bool initialize)
    {
        var backup = new DirectoryInfo(Backup(path));
        ProtectedInstallDirectory.Check(backup);
        string identityFile = Path.Combine(backup.FullName, "installation-owner");
        if (!File.Exists(identityFile) && !backup.EnumerateFileSystemInfos().Any())
        {
            if (!initialize) return false;
            using var stream = new FileStream(identityFile, FileMode.CreateNew, FileAccess.Write);
            using var writer = new StreamWriter(stream);
            writer.Write(BackupIdentity(path));
        }
        ProtectedInstallDirectory.Check(new FileInfo(identityFile));
        if (!string.Equals(File.ReadAllText(identityFile),
            BackupIdentity(path), StringComparison.OrdinalIgnoreCase))
            throw new IOException("原位升级备份目录与当前安装不匹配：" + backup.FullName);
        return true;
    }

    private static bool IsComplete(string path)
    {
        var marker = new FileInfo(Path.Combine(Backup(path), "complete"));
        if (!marker.Exists) return false;
        ProtectedInstallDirectory.Check(marker);
        return true;
    }

    internal static void CopyUserData(string path)
    {
        if (!IsPending(path)) return;
        string previous = Previous(path);
        string? data = null;
        RunAsDesktopUser(() => data = FindUserData(path, previous));
        if (data != null)
        {
            Directory.CreateDirectory(Path.Combine(path, "data"));
            ProtectedInstallDirectory.EnableDataFolder(path, "data");
        }
        RunAsDesktopUser(() =>
        {
            CheckLegacyRoot(previous);
            CopyTree(Path.Combine(previous, "plugins"), Path.Combine(path, "plugins"));
            if (data != null) CopyTree(data, Path.Combine(path, "data"));
        });
    }

    internal static void Commit(string path)
    {
        if (IsPending(path)) File.WriteAllText(Path.Combine(Backup(path), "complete"), "Open-Revo");
    }

    internal static void Restore(string path)
    {
        if (!IsPending(path)) return;
        ProtectedInstallDirectory.CheckLocation(path);
        CheckLegacyRoot(Previous(path));
        if (Directory.Exists(path))
        {
            ProtectedInstallDirectory.Check(new DirectoryInfo(path));
            if (InstalledLaunchTasks.HasInstallation(path)) InstalledLaunchTasks.Remove(path);
            // Quarantine the incomplete payload so nothing needs recursive deletion.
            Directory.Move(path, Path.Combine(Backup(path), "incomplete-" + Guid.NewGuid().ToString("N")));
        }
        Directory.Move(Previous(path), path);
    }

    private static void CheckLegacyRoot(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory ||
            !File.Exists(Path.Combine(path, "OpenRevo.Native.exe")) ||
            !File.Exists(Path.Combine(path, "unins000.dat")))
            throw new IOException("无法确认原安装目录：" + path);
    }

    private static string? FindUserData(string path, string previous)
    {
        string portable = Path.Combine(previous, "data");
        if (File.Exists(Path.Combine(portable, DataMarker))) return portable;
        string pointer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenRevoNative.location.json");
        if (File.Exists(pointer) && new FileInfo(pointer).Length < 8192)
        {
            string? location = JsonSerializer.Deserialize<string>(File.ReadAllText(pointer));
            if (location != null && Path.IsPathFullyQualified(location))
            {
                string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
                if (string.Equals(full, path, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("当前数据目录与程序目录重合。已保留旧目录，无法自动分离其中的文件。");
                if (full.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    string relative = Path.GetRelativePath(path, full);
                    string candidate = Path.Combine(previous, relative);
                    for (var current = new DirectoryInfo(candidate); current.FullName != previous; current = current.Parent!)
                        if ((File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("旧数据位置含链接：" + current.FullName);
                    if (File.Exists(Path.Combine(candidate, DataMarker))) return candidate;
                }
            }
        }
        return Directory.Exists(portable) ? portable : null;
    }

    private static void CopyTree(string source, string destination)
    {
        if (!Directory.Exists(source)) return;
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("旧插件或数据目录含链接：" + source);
        Directory.CreateDirectory(destination);
        foreach (var item in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("旧插件或数据含链接：" + item.FullName);
            string target = Path.Combine(destination, item.Name);
            if (item is DirectoryInfo) CopyTree(item.FullName, target);
            else File.Copy(item.FullName, target, overwrite: false);
        }
    }

    private static void RunAsDesktopUser(Action action)
    {
        nint shell = GetShellWindow();
        GetWindowThreadProcessId(shell, out uint processId);
        if (processId == 0) throw new IOException("无法取得当前桌面用户的普通权限。");
        using var process = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process.IsInvalid || !OpenProcessToken(process, 0x000A, out var token)) // TOKEN_DUPLICATE | TOKEN_QUERY
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        using (var desktop = new WindowsIdentity(token.DangerousGetHandle()))
        using (var installer = WindowsIdentity.GetCurrent())
        {
            if (desktop.User != installer.User || new WindowsPrincipal(desktop).IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("无法使用同一账户的普通权限迁移用户数据。");
            WindowsIdentity.RunImpersonated(token, action);
        }
    }

    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, uint processId);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
