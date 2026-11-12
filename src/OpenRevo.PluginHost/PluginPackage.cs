using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenRevo.PluginSdk;

namespace OpenRevo.PluginHost;

public sealed record PluginManifest(int ApiMajor, string Id, string Name, string Version,
    string EntryAssembly, string EntryType, string[] Permissions, bool Background,
    PluginPlatformManifest? Platform = null, string? Description = null, string? Author = null,
    int MinimumApiMinor = 0);

/// <summary>Validated source package, not authority to run. Grants come from trusted user settings.</summary>
public sealed class PluginPackage
{
    private readonly PluginManifest manifest;
    public PluginManifest Manifest => JsonSerializer.Deserialize<PluginManifest>(JsonSerializer.SerializeToUtf8Bytes(manifest, Json), Json)!;
    public string DirectoryPath { get; }
    public string Digest { get; }
    internal IReadOnlyDictionary<string, string> Files { get; }
    internal static readonly JsonSerializerOptions Json = CreateJson();
    private static JsonSerializerOptions CreateJson() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true
    };
    private PluginPackage(string directory, PluginManifest manifest, Dictionary<string, string> files, string digest)
        => (DirectoryPath, this.manifest, Files, Digest) = (directory, manifest, files, digest);

    public static PluginPackage Read(string directory)
    {
        string root = Path.GetFullPath(directory);
        RejectReparseAncestors(root);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (string path in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new InvalidDataException("Plugin packages must contain regular top-level files only.");
            string name = Path.GetFileName(path);
            if (!SafeFileName(name) || !(name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name == "plugin.json"))
                throw new InvalidDataException("Unsupported plugin package file.");
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            total += file.Length;
            if (files.Count >= 64 || total > 16 * 1024 * 1024)
                throw new InvalidDataException("Plugin package exceeds file or byte limits.");
            files.Add(name, Convert.ToHexString(SHA256.HashData(file)));
        }
        string manifestPath = Path.Combine(root, "plugin.json");
        using var input = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 16384) throw new InvalidDataException("Plugin manifest too large.");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(input, Json)
            ?? throw new InvalidDataException("Plugin manifest missing.");
        if (manifest.ApiMajor != PluginProtocol.Major || !Identifier(manifest.Id) ||
            manifest.MinimumApiMinor < 0 || manifest.MinimumApiMinor > PluginProtocol.Minor ||
            manifest.Name is not { Length: > 0 and <= 80 } ||
            manifest.Description?.Length > 512 || manifest.Author?.Length > 80 ||
            !Version.TryParse(manifest.Version, out _) || manifest.Version.Length > 32 ||
            !ValidEntry(manifest, files) ||
            manifest.Permissions is not { Length: <= 16 } ||
            manifest.Permissions.Any(p => p == null || !PluginPermissions.IsKnown(p)) ||
            manifest.Permissions.Distinct(StringComparer.Ordinal).Count() != manifest.Permissions.Length ||
            manifest.Background && !manifest.Permissions.Contains(PluginPermissions.Background))
            throw new InvalidDataException("Invalid or unsupported plugin manifest.");
        if (manifest.Platform != null) PlatformValidation.Validate(manifest.Platform, manifest.Permissions);
        // Re-hash the manifest read above: replacement between discovery and parse is rejected.
        input.Position = 0;
        if (!files.TryGetValue("plugin.json", out string? recorded) ||
            Convert.ToHexString(SHA256.HashData(input)) != recorded)
            throw new InvalidDataException("Plugin package changed during inspection.");
        string digestText = string.Join("\n", files.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + ":" + p.Value));
        string digest = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(digestText)));
        return new(root, manifest, files, digest);
    }

    private static bool ValidEntry(PluginManifest manifest, Dictionary<string, string> files)
    {
        if (manifest.Platform?.Runtime == "static") return manifest.EntryAssembly == "" && manifest.EntryType == "" && !manifest.Background;
        return SafeFileName(manifest.EntryAssembly) && manifest.EntryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
            files.ContainsKey(manifest.EntryAssembly) && manifest.EntryType is { Length: > 0 and <= 160 } &&
            manifest.EntryType.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_');
    }

    internal static bool Identifier(string? value) => value is { Length: > 0 and <= 64 } &&
        char.IsAsciiLetterOrDigit(value[0]) && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');
    private static bool SafeFileName(string? value) => value is { Length: > 0 and <= 120 } && value != "." && value != ".." &&
        !value.StartsWith('.') && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    internal static void RejectReparseAncestors(string path)
    {
        for (var part = new DirectoryInfo(Path.GetFullPath(path)); part != null; part = part.Parent)
            if (part.Exists && (part.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Reparse points are not accepted in plugin paths.");
    }
}
