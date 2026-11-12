namespace OpenRevo.PluginHost;

public sealed record PluginDiscoveryEntry(string FolderName, PluginPackage? Package, string? Error);
public sealed record PluginDiscoveryResult(PluginDiscoveryEntry[] Entries, string? RootError);

public sealed class PluginDiscovery
{
    public string Root { get; }
    public PluginDiscovery(string installationDirectory) => Root = Path.Combine(Path.GetFullPath(installationDirectory), "plugins");

    public string EnsureRoot()
    {
        PluginPackage.RejectReparseAncestors(Root);
        Directory.CreateDirectory(Root);
        PluginPackage.RejectReparseAncestors(Root);
        return Root;
    }

    public PluginDiscoveryResult Refresh()
    {
        try
        {
            PluginPackage.RejectReparseAncestors(Root);
            if (!Directory.Exists(Root)) return new([], "插件目录不存在：" + Root);
            var folders = Directory.EnumerateDirectories(Root).Take(129).ToArray();
            if (folders.Length > 128) return new([], "插件子文件夹超过128项，请整理目录后刷新。");
            var result = new List<PluginDiscoveryEntry>();
            foreach (var folder in folders.Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var package = PluginPackage.Read(folder);
                    result.Add(new(Path.GetFileName(folder), package, null));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or InvalidDataException)
                { result.Add(new(Path.GetFileName(folder), null, error.Message)); }
            }
            var duplicates = result.Where(e => e.Package != null).GroupBy(e => e.Package!.Manifest.Id, StringComparer.Ordinal)
                .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            return new(result.Select(e => e.Package != null && duplicates.Contains(e.Package.Manifest.Id)
                ? new PluginDiscoveryEntry(e.FolderName, null, "插件ID重复，请只保留一份后刷新。") : e).ToArray(), null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { return new([], error.Message); }
    }

    internal void VerifyLocation(PluginPackage package)
    {
        var parent = Path.GetDirectoryName(package.DirectoryPath);
        if (!string.Equals(parent, Root, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Only the installation plugins directory participates in discovery.");
        PluginPackage.RejectReparseAncestors(package.DirectoryPath);
    }
}
