using System.Text.Json;
using OpenRevo.Core;
using OpenRevo.PluginSdk;

namespace OpenRevo.PluginHost;

public sealed record PluginRecord(string Id, string Digest, bool Enabled, string? Fault,
    string[] PausedPolicies, PluginThemeValue[] ThemeEdits, PluginKeyMapping[] KeyEdits, string? ActiveRun = null);
public sealed record PluginNavigationPreference(string Id, bool Visible, int Order);
public sealed record PluginPendingRestore(string PluginId, string Resource, PluginAction Baseline, string Reason);
public sealed record PluginPlatformDocument(int Version, PluginRecord[] Plugins,
    PluginNavigationPreference[] Navigation, PluginPendingRestore[] PendingRestores);

public sealed class PluginPlatformStore
{
    private readonly string path;
    private PluginPlatformDocument current;
    public PluginPlatformStore(string dataDirectory)
    {
        path = Path.Combine(Path.GetFullPath(dataDirectory), "platform-state.json");
        PluginPackage.RejectReparseAncestors(Path.GetDirectoryName(path)!);
        current = File.Exists(path) ? Read() : new(1, [], [], []);
    }
    public PluginPlatformDocument Snapshot => Clone(current);
    public PluginRecord? FindPlugin(string id)
    {
        var record = current.Plugins.FirstOrDefault(p => p.Id == id);
        return record == null ? null : Clone(record);
    }
    public void Commit(PluginPlatformDocument document)
    {
        Validate(document);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, PluginPackage.Json);
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException("Plugin state exceeds its byte budget.");
        var next = Clone(document);
        PluginPackage.RejectReparseAncestors(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Invalid plugin state file.");
        if (File.Exists(path)) _ = Read(); // Never overwrite a record that became corrupt or changed schema after loading.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PluginSandbox.ProtectDirectory(Path.GetDirectoryName(path)!);
        AtomicPreferenceFile.Write(path, System.Text.Encoding.UTF8.GetString(bytes));
        current = next;
    }
    private PluginPlatformDocument Read()
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Invalid state path.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 1024 * 1024) throw new InvalidDataException("Plugin state too large.");
        var value = JsonSerializer.Deserialize<PluginPlatformDocument>(file, PluginPackage.Json)
            ?? throw new InvalidDataException("Missing plugin state.");
        Validate(value); return value;
    }
    private static PluginPlatformDocument Clone(PluginPlatformDocument value) =>
        JsonSerializer.Deserialize<PluginPlatformDocument>(JsonSerializer.SerializeToUtf8Bytes(value, PluginPackage.Json), PluginPackage.Json)!;
    private static PluginRecord Clone(PluginRecord value) =>
        JsonSerializer.Deserialize<PluginRecord>(JsonSerializer.SerializeToUtf8Bytes(value, PluginPackage.Json), PluginPackage.Json)!;
    private static void Validate(PluginPlatformDocument value)
    {
        if (value.Version != 1 || value.Plugins is not { Length: <= 128 } || value.Navigation is not { Length: <= 1032 } ||
            value.PendingRestores is not { Length: <= 384 } || value.Plugins.Select(p => p.Id).Distinct().Count() != value.Plugins.Length ||
            value.Plugins.Any(p => !PluginPackage.Identifier(p.Id) || p.Digest is not { Length: 64 } || !p.Digest.All(char.IsAsciiHexDigit) ||
                p.Fault?.Length > 512 || p.ActiveRun?.Length > 64 || p.PausedPolicies is not { Length: <= 8 } || p.PausedPolicies.Any(id => !PluginPackage.Identifier(id)) ||
                p.ThemeEdits is not { Length: <= 80 } || p.KeyEdits is not { Length: <= 16 }) ||
            value.Navigation.Any(p => p.Id is not { Length: > 0 and <= 160 } || p.Order is < 0 or > 1032) ||
            value.Navigation.Select(p => p.Id).Distinct().Count() != value.Navigation.Length)
            throw new InvalidDataException("Invalid plugin platform state.");
        foreach (var pending in value.PendingRestores)
        {
            if (!PluginPackage.Identifier(pending.PluginId) || pending.Reason is not { Length: > 0 and <= 512 } ||
                pending.Resource != PlatformValidation.ActionResource(pending.Baseline)) throw new InvalidDataException("Invalid pending restore.");
            PlatformValidation.ValidateAction(pending.Baseline);
        }
    }
}
