using System.Text.Json;
using OpenRevo.Core;
using OpenRevo.PluginSdk;

namespace OpenRevo.PluginHost;

/// <summary>Created by trusted user-consent UI, never deserialized from the plugin's IPC.</summary>
public sealed class PluginApproval
{
    public string PluginId { get; }
    public string PackageDigest { get; }
    private readonly string[] permissions;
    public string[] Permissions => permissions.ToArray();
    private PluginApproval(string id, string digest, string[] permissions)
        => (PluginId, PackageDigest, this.permissions) = (id, digest, permissions);
    public static PluginApproval FromUserSelection(PluginPackage reviewedPackage, IEnumerable<string> selectedPermissions)
    {
        string[] selected = selectedPermissions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var manifest = reviewedPackage.Manifest;
        if (selected.Any(p => !PluginPermissions.IsKnown(p) || !manifest.Permissions.Contains(p)))
            throw new InvalidDataException("Only known requested permissions may be approved.");
        if (manifest.Background && !selected.Contains(PluginPermissions.Background))
            throw new InvalidDataException("This plugin requires an explicit background grant.");
        return new(manifest.Id, reviewedPackage.Digest, selected);
    }
    public bool Matches(PluginPackage package) => PluginId == package.Manifest.Id && PackageDigest == package.Digest;
}

/// <summary>Optional persistence. Saving consent never starts a plugin or grants newly added permissions.</summary>
public sealed class PluginApprovalStore(string dataRoot)
{
    private readonly string directory = Path.Combine(Path.GetFullPath(dataRoot), "approvals");
    private sealed record StoredApproval(int Version, string PluginId, string PackageDigest, string[] Permissions);
    public void Save(PluginApproval approval)
    {
        PluginPackage.RejectReparseAncestors(directory);
        Directory.CreateDirectory(directory); PluginSandbox.ProtectDirectory(directory);
        string path = Path.Combine(directory, approval.PluginId + ".json");
        RejectLink(path);
        if (File.Exists(path)) _ = Read(path); // preserve corrupt or unknown-version host records
        string text = JsonSerializer.Serialize(new StoredApproval(1, approval.PluginId, approval.PackageDigest, approval.Permissions), PluginPackage.Json);
        AtomicPreferenceFile.Write(path, text);
    }
    public PluginApproval? Load(PluginPackage currentPackage)
    {
        string path = Path.Combine(directory, currentPackage.Manifest.Id + ".json");
        if (!File.Exists(path)) return null;
        var stored = Read(path);
        if (stored.PluginId != currentPackage.Manifest.Id || stored.PackageDigest != currentPackage.Digest) return null;
        return PluginApproval.FromUserSelection(currentPackage, stored.Permissions);
    }
    public void Revoke(string pluginId)
    {
        if (!PluginPackage.Identifier(pluginId)) throw new ArgumentException("Invalid plugin ID.");
        PluginPackage.RejectReparseAncestors(directory);
        string path = Path.Combine(directory, pluginId + ".json"); RejectLink(path);
        // The runtime must be disabled first. Failure to delete is reported, never treated as success.
        File.Delete(path);
    }
    private StoredApproval Read(string path)
    {
        PluginPackage.RejectReparseAncestors(directory); RejectLink(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 16384) throw new InvalidDataException("Approval record too large.");
        var stored = JsonSerializer.Deserialize<StoredApproval>(file, PluginPackage.Json);
        if (stored is not { Version: 1, Permissions.Length: <= 16 } || !PluginPackage.Identifier(stored.PluginId) ||
            stored.PackageDigest is not { Length: 64 } || !stored.PackageDigest.All(char.IsAsciiHexDigit) ||
            stored.Permissions.Any(p => !PluginPermissions.IsKnown(p))) throw new InvalidDataException("Unsupported approval record.");
        return stored;
    }
    private static void RejectLink(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Approval file cannot be a reparse point.");
    }
}
