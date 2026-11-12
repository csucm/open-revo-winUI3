using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OpenRevo.Core;

internal static class ProtectedInstallDirectory
{
    private const string OwnerRights = "S-1-3-4";
    private const string ProtectedAcl = "O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GRGX;;;BU)(A;OICI;RC;;;OW)";
    private static readonly string[] Trusted = ["S-1-5-18", "S-1-5-32-544",
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"];

    internal static void Prepare(string path, bool create, bool preflight = false)
    {
        var directory = CheckLocation(path);
        if (!directory.Exists && create) CreateProtected(directory);
        Check(directory);
        if (preflight && !InstalledLaunchTasks.HasInstallation(directory.FullName) &&
            directory.EnumerateFileSystemInfos().Any(item => item is not DirectoryInfo ||
                !string.Equals(item.Name, "plugins", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("该目录存在未登记的文件。请使用 Open-Revo 已登记的安装目录。");
        CheckTree(directory, top: true);
        InstalledLaunchTasks.RequireOwner(directory.FullName, allowNew: create);
    }

    internal static DirectoryInfo CheckLocation(string path)
    {
        InstalledLaunchTasks.RequireAdministrator();
        var directory = new DirectoryInfo(path);
        var volume = new DriveInfo(directory.Root.FullName);
        if (directory.Parent == null || directory.Root.FullName.Length != 3 ||
            volume.DriveType != DriveType.Fixed || volume.DriveFormat != "NTFS" ||
            path.Contains('%') || path.Contains('$') || path[3..].Contains(':'))
            throw new IOException("请选择固定 NTFS 盘上的普通文件夹，路径不能含 % 或 $。");

        var parents = new Stack<DirectoryInfo>();
        for (var parent = directory.Parent; parent != null; parent = parent.Parent) parents.Push(parent);
        foreach (var parent in parents)
        {
            if (!parent.Exists) throw new IOException("安装目录的上一级必须已存在并受到保护。");
            Check(parent, volumeRoot: parent.Parent == null);
        }

        return directory;
    }

    internal static void CreateProtected(DirectoryInfo directory)
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(ProtectedAcl);
        directory.Create(security);
    }

    internal static void EnableDataFolder(string path, string name)
    {
        var folder = new DirectoryInfo(Path.Combine(path, name));
        Check(folder, writableContents: true);
        using var identity = WindowsIdentity.GetCurrent();
        var security = folder.GetAccessControl();
        // Keep the folder itself protected against replacement. Only descendants get Modify.
        security.SetAccessRule(new FileSystemAccessRule(identity.User!,
            FileSystemRights.ReadAndExecute | FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.InheritOnly, AccessControlType.Allow));
        folder.SetAccessControl(security);
    }

    private static void CheckTree(DirectoryInfo directory, bool top)
    {
        Check(directory);
        foreach (var item in directory.EnumerateFileSystemInfos())
        {
            if (top && item is DirectoryInfo &&
                (string.Equals(item.Name, "plugins", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(item.Name, "data", StringComparison.OrdinalIgnoreCase)))
            {
                Check(item, writableContents: true);
                continue;
            }
            if (item is DirectoryInfo child) CheckTree(child, top: false);
            else Check(item);
        }
    }

    internal static void Check(FileSystemInfo item, bool volumeRoot = false, bool writableContents = false)
    {
        // DirectoryInfo.Exists can cache a missing path before the ACL-aware creation.
        var attributes = File.GetAttributes(item.FullName);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("安装位置或程序文件含链接：" + item.FullName);
        const AccessControlSections sections = AccessControlSections.Owner | AccessControlSections.Access;
        FileSystemSecurity security = item is DirectoryInfo folder
            ? folder.GetAccessControl(sections) : ((FileInfo)item).GetAccessControl(sections);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        bool restrictedOwner = rules.Any(rule => rule.IdentityReference.Value == OwnerRights &&
            rule.AccessControlType == AccessControlType.Allow && !rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly));
        if (!Trusted.Contains(security.GetOwner(typeof(SecurityIdentifier))!.Value) && !restrictedOwner)
            throw new IOException("安装位置或文件的所有者可修改权限：" + item.FullName);

        const int genericAll = 0x10000000, genericWrite = 0x40000000;
        int forbidden = genericAll | (int)(FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership |
            FileSystemRights.DeleteSubdirectoriesAndFiles);
        if (!volumeRoot) forbidden |= (int)FileSystemRights.Delete;
        if (!volumeRoot && !writableContents) forbidden |= genericWrite | (int)FileSystemRights.Write;
        // Reparse data requires write access on the directory itself.
        if (writableContents) forbidden |= genericWrite | (int)(FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes);
        foreach (var rule in rules)
        {
            if (rule.AccessControlType == AccessControlType.Allow && !Trusted.Contains(rule.IdentityReference.Value) &&
                !rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly) && ((int)rule.FileSystemRights & forbidden) != 0)
                throw new IOException("普通用户可修改或替换管理员启动文件：" + item.FullName);
        }
    }
}
