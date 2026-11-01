using System.Text.Json;
namespace OpenRevo.Core;
public static class UserDataStore
{
    private const string Marker="openrevo-native-data.json";
    public static string ValidateDirectory(string directory)
    {
        if(!Path.IsPathFullyQualified(directory))throw new ArgumentException("请选择绝对路径。");
        string path=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        if(path.Length<=Path.GetPathRoot(path)!.Length||new DirectoryInfo(path).Parent==null)throw new ArgumentException("不能使用磁盘根目录。");
        for(var d=new DirectoryInfo(path);d!=null;d=d.Parent)if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("存储目录不能包含链接或重解析点。");
        return path;
    }
    public static void EnsureMarker(string? folder=null)
    {string root=ValidateDirectory(folder??AppData.DirectoryPath);Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,Marker),"{\"application\":\"OpenRevo.Native\",\"schema\":1}");}
    private static void ValidateManaged(string root)
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,Marker)));
        if(doc.RootElement.GetProperty("application").GetString()!="OpenRevo.Native")throw new IOException("该目录不属于本版原生控制台。");
    }
    public static string Migrate(string source,string destination,string? pointer=null)
    {
        source=ValidateDirectory(source);destination=ValidateDirectory(destination);ValidateManaged(source);
        bool Inside(string a,string b)=>a.StartsWith(b+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||a.Equals(b,StringComparison.OrdinalIgnoreCase);
        if(Inside(source,destination)||Inside(destination,source))throw new IOException("新旧目录不能互相包含。");
        if(Directory.Exists(destination)&&Directory.EnumerateFileSystemEntries(destination).Any())throw new IOException("目标目录必须为空，未覆盖任何文件。");
        // Reject links before copying; fixed limits prevent accidentally copying a whole unrelated tree.
        var files=new List<(string Path,string Relative)>();long bytes=0;
        void Scan(string directory)
        {
            foreach(string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes=File.GetAttributes(entry);if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("数据包含链接，迁移已停止。");
                if((attributes&FileAttributes.Directory)!=0)Scan(entry);else{bytes+=new FileInfo(entry).Length;files.Add((entry,Path.GetRelativePath(source,entry)));if(files.Count>20000||bytes>512L*1024*1024)throw new IOException("数据超过迁移边界（512MiB/20000文件）。");}
            }
        }
        Scan(source);Directory.CreateDirectory(destination);
        foreach(var file in files)
        {
            string target=Path.Combine(destination,file.Relative);Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file.Path,target,false);
            using var original=File.OpenRead(file.Path);using var copied=File.OpenRead(target);
            if(!System.Security.Cryptography.SHA256.HashData(original).SequenceEqual(System.Security.Cryptography.SHA256.HashData(copied)))throw new IOException("迁移校验失败，旧目录保持完整。");
        }
        ValidateManaged(destination);
        string location=pointer??AppData.LocationPointer;Directory.CreateDirectory(Path.GetDirectoryName(location)!);string temp=location+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(destination));File.Move(temp,location,true);return destination;
    }
    public static string Reset(string directory)
    {
        string root=ValidateDirectory(directory);ValidateManaged(root);
        string backup=root+".backup-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..8];
        // Move only the exact marked app directory to an adjacent backup. No recursive deletion.
        Directory.Move(root,backup);Directory.CreateDirectory(root);EnsureMarker(root);return backup;
    }
}
