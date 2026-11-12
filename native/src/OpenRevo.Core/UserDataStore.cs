using System.Text.Json;
namespace OpenRevo.Core;
public static class UserDataStore
{
    private const string Marker="openrevo-native-data.json";
    private const string PendingReset = "reset-pending.json";
    public static string ValidateDirectory(string directory)
    {
        if(!Path.IsPathFullyQualified(directory))throw new ArgumentException("请选择绝对路径。");
        string path=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        if(path.Length<=Path.GetPathRoot(path)!.Length||new DirectoryInfo(path).Parent==null)throw new ArgumentException("不能使用磁盘根目录。");
        for(var d=new DirectoryInfo(path);d!=null;d=d.Parent)if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("存储目录不能包含链接或重解析点。");
        return path;
    }
    public static void EnsureMarker(string? folder=null)
    {
        string root=ValidateDirectory(folder??AppData.DirectoryPath);Directory.CreateDirectory(root);
        string marker=Path.Combine(root,Marker);
        if(File.Exists(marker)){ValidateManaged(root);return;}
        using var stream=new FileStream(marker,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        stream.Write("{\"application\":\"OpenRevo.Native\",\"schema\":1}"u8);
    }
    public static void ValidateManaged(string root)
    {
        root=ValidateDirectory(root);
        var marker=new FileInfo(Path.Combine(root,Marker));
        if(!marker.Exists||marker.Length>4096||(marker.Attributes&FileAttributes.ReparsePoint)!=0)
            throw new IOException("缺少可验证的应用数据标记。");
        using var doc=JsonDocument.Parse(File.ReadAllText(marker.FullName));
        if(doc.RootElement.ValueKind!=JsonValueKind.Object||
            !doc.RootElement.TryGetProperty("application",out var application)||application.ValueKind!=JsonValueKind.String||
            application.GetString()!="OpenRevo.Native"||!doc.RootElement.TryGetProperty("schema",out var schema)||
            schema.ValueKind!=JsonValueKind.Number||!schema.TryGetInt32(out int version)||version!=1)
            throw new IOException("该目录不属于本版原生控制台。");
    }

    public static string ValidateInstalledDataLocation(string directory, string installation, bool allowPortable = false)
    {
        string path = ValidateDirectory(directory);
        if (Path.GetPathRoot(path)?.Length != 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' ||
            path.AsSpan(3).Contains(':') || new DriveInfo(path[..3]).DriveType == DriveType.Network)
            throw new ArgumentException("请选择本机磁盘上的普通数据目录。");
        installation = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installation));
        if (allowPortable && path.Equals(Path.Combine(installation, "data"), StringComparison.OrdinalIgnoreCase))
            return path;
        if (Within(path, installation) || Within(installation, path))
            throw new IOException("数据目录不能与安装目录重合或互相包含。");
        return path;
    }

    private static bool Within(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static string MigrateInstalled(string source, string destination, string installation)
    {
        source = ValidateInstalledDataLocation(source, installation, allowPortable: true);
        destination = ValidateInstalledDataLocation(destination, installation);
        return Migrate(source, destination, AppData.CommittedLocationPointer);
    }

    public static string ResetInstalledPortable(string installation)
    {
        StartupRegistration.ValidateResidentInstallation(installation);
        string source = Path.Combine(installation, "data");
        RecoverInstalledPortableReset(installation);
        ProtectedInstallDirectory.Check(new DirectoryInfo(source), writableContents: true);
        ValidateManaged(source);
        // Keep the backup outside the installation tree. Its contents remain
        // user data and are never checked or loaded as installed binaries.
        var backups = ProtectedInstallDirectory.CheckLocation(installation + ".openrevo-data-backups");
        if (!backups.Exists) ProtectedInstallDirectory.CreateProtected(backups);
        ProtectedInstallDirectory.Check(backups);
        string id = Guid.NewGuid().ToString("N");
        string empty = Path.Combine(backups.FullName, "data.reset-" + id);
        string backup = Path.Combine(backups.FullName, "data.backup-" + id);
        string pending = Path.Combine(backups.FullName, PendingReset);
        AtomicPreferenceFile.Write(pending, JsonSerializer.Serialize(id));
        try
        {
            return ReplaceWithEmpty(source, backup, empty, () =>
            {
                ProtectedInstallDirectory.CreateProtected(new DirectoryInfo(empty));
                EnsureMarker(empty);
                ProtectedInstallDirectory.EnableDataFolder(backups.FullName, Path.GetFileName(empty));
            });
        }
        finally
        {
            // When rollback fails, keep the record for the next launch.
            if (Directory.Exists(source)) TryStorageCleanup(() => File.Delete(pending));
        }
    }

    public static void RecoverInstalledPortableReset(string installation)
    {
        string backups = installation + ".openrevo-data-backups";
        if (!Directory.Exists(backups)) return;
        var protectedRoot = ProtectedInstallDirectory.CheckLocation(backups);
        ProtectedInstallDirectory.Check(protectedRoot);
        var pending = new FileInfo(Path.Combine(backups, PendingReset));
        if (!pending.Exists) return;
        StartupRegistration.ValidateResidentInstallation(installation);
        ProtectedInstallDirectory.Check(pending);
        if (pending.Length > 128 || JsonSerializer.Deserialize<string>(File.ReadAllText(pending.FullName)) is not { } id ||
            !Guid.TryParseExact(id, "N", out _))
            throw new IOException("无法验证待恢复的 Open-Revo 数据备份。");

        string source = Path.Combine(installation, "data");
        string backup = Path.Combine(backups, "data.backup-" + id);
        if (!Directory.Exists(source))
        {
            ProtectedInstallDirectory.Check(new DirectoryInfo(backup), writableContents: true);
            ValidateManaged(backup);
            Directory.Move(backup, source);
        }
        string empty = Path.Combine(backups, "data.reset-" + id);
        if (Directory.Exists(empty))
        {
            ProtectedInstallDirectory.Check(new DirectoryInfo(empty), writableContents: true);
            Directory.Move(empty, Path.Combine(backups, "data.interrupted-" + id));
        }
        File.Delete(pending.FullName);
    }

    public static string Migrate(string source,string destination,string? pointer=null)
    {
        source=ValidateDirectory(source);destination=ValidateDirectory(destination);ValidateManaged(source);
        if (pointer == null && File.Exists(AppData.CommittedLocationPointer))
        {
            destination = ValidateInstalledDataLocation(destination, AppContext.BaseDirectory);
            pointer = AppData.CommittedLocationPointer;
        }
        if(Within(source,destination)||Within(destination,source))throw new IOException("新旧目录不能互相包含。");
        if(Directory.Exists(destination)&&Directory.EnumerateFileSystemEntries(destination).Any())throw new IOException("目标目录必须为空，未覆盖任何文件。");
        string staging=destination+".copy-"+Guid.NewGuid().ToString("N");
        var copiedFiles=new List<string>();
        var copiedDirectories=new List<string>();
        long bytes=0;
        bool promoted=false;
        try
        {
            Directory.CreateDirectory(staging);
            copiedDirectories.Add(staging);
            var pending=new Stack<string>();
            pending.Push(source);
            while(pending.TryPop(out string? directory))
            {
                ValidateDirectory(directory);
                foreach(string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    if(copiedFiles.Count+copiedDirectories.Count>20000)
                        throw new IOException("数据超过迁移边界（512 MiB / 20000 项）。");
                    var attributes=File.GetAttributes(entry);
                    if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("数据包含链接，迁移已停止。");
                    string target=Path.Combine(staging,Path.GetRelativePath(source,entry));
                    ValidateDirectory(Path.GetDirectoryName(target)!);
                    if((attributes&FileAttributes.Directory)!=0)
                    {
                        Directory.CreateDirectory(target);
                        copiedDirectories.Add(target);
                        pending.Push(entry);
                    }
                    else
                    {
                        using var original=new FileStream(entry,FileMode.Open,FileAccess.Read,FileShare.Read);
                        bytes+=original.Length;
                        if(bytes>512L*1024*1024)throw new IOException("数据超过迁移边界（512 MiB / 20000 项）。");
                        using(var copy=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                        {
                            copiedFiles.Add(target);
                            original.CopyTo(copy);
                        }
                        original.Position=0;
                        using var copied=File.OpenRead(target);
                        if(!System.Security.Cryptography.SHA256.HashData(original).SequenceEqual(System.Security.Cryptography.SHA256.HashData(copied)))
                            throw new IOException("迁移校验失败，旧目录保持完整。");
                    }
                }
            }
            ValidateManaged(staging);
            ValidateDirectory(destination);
            bool wasEmpty=Directory.Exists(destination);
            if(wasEmpty)Directory.Delete(destination); // Nonrecursive. Concurrent new files make this fail.
            try { Directory.Move(staging,destination); }
            catch
            {
                if(wasEmpty&&!Directory.Exists(destination))Directory.CreateDirectory(destination);
                throw;
            }
            promoted=true;
            try { SaveLocation(pointer ?? AppData.LocationPointer, destination); }
            catch(Exception error)when(error is IOException or UnauthorizedAccessException)
            {throw new IOException("数据已复制并校验，但新位置保存失败。完整副本保留在："+destination,error);}
            return destination;
        }
        finally
        {
            if(!promoted)
            {
                foreach(string file in copiedFiles) TryStorageCleanup(() =>
                {
                    ValidateDirectory(Path.GetDirectoryName(file)!);
                    if((File.GetAttributes(file)&FileAttributes.ReparsePoint)==0)File.Delete(file);
                });
                foreach(string directory in copiedDirectories.AsEnumerable().Reverse()) TryStorageCleanup(() =>
                    Directory.Delete(ValidateDirectory(directory)));
            }
        }
    }
    private static void SaveLocation(string pointer, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(pointer)!);
        AtomicPreferenceFile.Write(pointer, JsonSerializer.Serialize(destination));
    }
    public static string Reset(string directory)
    {
        string root=ValidateDirectory(directory);ValidateManaged(root);
        string backup=Path.Combine(Path.GetDirectoryName(root)!,BackupName(Path.GetFileName(root)));
        string empty=root+".reset-"+Guid.NewGuid().ToString("N");
        return ReplaceWithEmpty(root, backup, empty, () => EnsureMarker(empty));
    }

    private static string BackupName(string name) =>
        name+".backup-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..8];

    private static string ReplaceWithEmpty(string root, string backup, string empty, Action prepareEmpty)
    {
        try
        {
            prepareEmpty();
            // Prepare the replacement before moving the marked app directory.
            Directory.Move(root,backup);
            try { Directory.Move(empty,root); }
            catch(Exception error)
            {
                try { Directory.Move(backup,root); }
                catch(Exception restoreError)
                {
                    throw new IOException("数据已保留在备份目录，未能恢复原位置："+backup,
                        new AggregateException(error,restoreError));
                }
                throw;
            }
            return backup;
        }
        finally
        {
            TryStorageCleanup(() =>
            {
                if(Directory.Exists(empty))
                {
                    ValidateDirectory(empty);
                    File.Delete(Path.Combine(empty,Marker));
                    Directory.Delete(empty);
                }
            });
        }
    }

    private static void TryStorageCleanup(Action cleanup)
    {
        try { cleanup(); }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException)
        {DiagnosticLog.Failure("storage.staging_cleanup_failed",error);}
    }
}
