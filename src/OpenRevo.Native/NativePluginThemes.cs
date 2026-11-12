using OpenRevo.PluginHost;
using OpenRevo.PluginSdk;

namespace OpenRevo.Native;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, PluginPackage> folderThemePlugins = new(StringComparer.Ordinal);

    private void LoadFolderTheme(PluginPackage package, PluginApprovalStore approvals)
    {
        PluginThemePalette.ValidatePackage(package);
        string id = package.Manifest.Id;
        if (folderThemePlugins.TryGetValue(id, out var previous) && previous.Digest == package.Digest) return;
        var next = folderThemePlugins.Where(p => p.Key != id).Select(p => p.Value).Append(package).ToArray();
        var palette = PluginThemePalette.Compose(next); // Validate all slots and readability before changing the UI.
        if (approvals.Load(package) == null)
            approvals.Save(PluginApproval.FromUserSelection(package, [PluginPlatformPermissions.Theme]));
        NativePluginThemePalette.Apply(palette);
        folderThemePlugins[id] = package; folderPluginDigests[id] = package.Digest;
    }
    private async Task StopFolderPluginAsync(string id)
    {
        if (folderPlugins.TryGetValue(id, out var session) && session.View is { } view) FolderPluginCatalog.RememberView(id,view);
        if (folderThemePlugins.ContainsKey(id))
        {
            var palette = PluginThemePalette.Compose(folderThemePlugins.Where(p => p.Key != id).Select(p => p.Value));
            NativePluginThemePalette.Apply(palette);
            folderThemePlugins.Remove(id);
        }
        await (nativePluginRuntime?.DisableAsync(id) ?? Task.CompletedTask);
        folderPlugins.Remove(id); folderPluginDigests.Remove(id);
    }
    private void ClearFolderThemes()
    {
        NativePluginThemePalette.Apply(PluginThemePalette.Empty);
        foreach (string id in folderThemePlugins.Keys) folderPluginDigests.Remove(id);
        folderThemePlugins.Clear();
    }
}
