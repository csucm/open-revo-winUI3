using System.Diagnostics;
using OpenRevo.Core;

internal static class ResidentTransition
{
    // These root files belong to the former WinUI entry. Shared libraries,
    // assets and user directories can still belong to the current layout.
    private static readonly string[] LegacyFiles =
    [
        "OpenRevo.Native.exe", "OpenRevo.Native.dll", "OpenRevo.Native.deps.json",
        "OpenRevo.Native.runtimeconfig.json", "OpenRevo.Native.pri", "App.xbf"
    ];

    private const string FormerResident = "OpenRevo.Resident.exe";
    private static readonly string[] FormerPanelFiles =
    [
        "panel-native/OpenRevo.ResidentNativePanel.exe", "panel-native/OpenRevo.ResidentNativePanel.dll",
        "panel-native/OpenRevo.ResidentNativePanel.deps.json", "panel-native/OpenRevo.ResidentNativePanel.runtimeconfig.json",
        "panel-native/OpenRevo.ResidentNativePanel.pri"
    ];
    private static readonly string[] ArchivedFiles = [.. LegacyFiles, FormerResident, "OpenRevo.Sensors.exe", .. FormerPanelFiles];
    private static readonly Dictionary<string, string> ArchivePaths =
        ArchivedFiles.ToDictionary(name => Path.GetFileName(name), StringComparer.OrdinalIgnoreCase);

    internal static void RequireStopped()
    {
        foreach (string name in new[] { "OpenRevo.Native", "OpenRevo.Resident", "RevoCore", "OpenRevo.Sensors", "OpenRevo.ResidentNativePanel", "Open Revo" })
        {
            var instances = Process.GetProcessesByName(name);
            try
            {
                if (instances.Any(process => !process.HasExited))
                    throw new IOException("请先从托盘完全退出 Open-Revo，再继续安装或卸载。");
            }
            finally { foreach (var process in instances) process.Dispose(); }
        }
    }

    internal static void RetireLegacyConsole(string path)
    {
        RequireStopped();
        string[] retire = InstalledLaunchTasks.RequireResidentBootstrap(path) == "RevoCore.exe" ? ArchivedFiles : LegacyFiles;
        string backupPath = path + ".openrevo-retired-console";
        if (!Directory.Exists(backupPath) && !retire.Any(name => File.Exists(Path.Combine(path, name))))
            return;

        var backups = ProtectedInstallDirectory.CheckLocation(backupPath);
        if (!backups.Exists) ProtectedInstallDirectory.CreateProtected(backups);
        ProtectedInstallDirectory.Check(backups);
        var pending = new DirectoryInfo(Path.Combine(backups.FullName, "pending"));
        if (pending.Exists)
        {
            // A prior installer stopped after it put the resident files in place.
            // Keep that partial archive separate from any later legacy reinstall.
            ValidatePending(pending);
            Archive(backups, pending, "interrupted");
        }

        var files = retire.Select(name => new FileInfo(Path.Combine(path, name))).Where(file => file.Exists).ToArray();
        if (files.Length == 0) return;
        foreach (var file in files) ProtectedInstallDirectory.Check(file);
        ProtectedInstallDirectory.CreateProtected(pending);
        ProtectedInstallDirectory.Check(pending);
        try
        {
            foreach (var file in files)
                File.Move(file.FullName, Path.Combine(pending.FullName, file.Name));
            Archive(backups, pending, "retired");
        }
        catch (Exception error)
        {
            try { RestorePending(path, pending); }
            catch (Exception restoreError)
            {
                throw new IOException("旧控制台文件保留在待恢复目录：" + pending.FullName,
                    new AggregateException(error, restoreError));
            }
            throw;
        }
    }

    private static void Archive(DirectoryInfo backups, DirectoryInfo pending, string disposition)
    {
        string archive = disposition + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
        Directory.Move(pending.FullName, Path.Combine(backups.FullName, archive));
    }

    private static FileSystemInfo[] ValidatePending(DirectoryInfo pending)
    {
        ProtectedInstallDirectory.Check(pending);
        var files = pending.GetFileSystemInfos();
        foreach (var item in files)
        {
            if (item is not FileInfo || !ArchivePaths.ContainsKey(item.Name))
                throw new IOException("旧控制台备份中有无法识别的文件：" + item.FullName);
            ProtectedInstallDirectory.Check(item);
        }
        return files;
    }

    private static void RestorePending(string path, DirectoryInfo pending)
    {
        foreach (var item in ValidatePending(pending))
        {
            string original = Path.Combine(path, ArchivePaths[item.Name]);
            ProtectedInstallDirectory.Check(new DirectoryInfo(Path.GetDirectoryName(original)!));
            if (File.Exists(original) || Directory.Exists(original))
                throw new IOException("无法安全恢复旧控制台文件：" + item.FullName);
        }

        // Restore the executable last, after its assemblies and resources.
        foreach (string name in ArchivedFiles.Reverse())
        {
            string backup = Path.Combine(pending.FullName, Path.GetFileName(name));
            if (File.Exists(backup)) File.Move(backup, Path.Combine(path, name));
        }
        Directory.Delete(pending.FullName);
    }
}
