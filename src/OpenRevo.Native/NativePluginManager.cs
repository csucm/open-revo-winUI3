using OpenRevo.Core;
using OpenRevo.PluginHost;
using OpenRevo.PluginSdk;

namespace OpenRevo.Native;

public sealed partial class MainWindow
{
    private PluginFolderCatalog? folderPluginCatalog;
    private PluginFolderCatalog FolderPluginCatalog => folderPluginCatalog ??= new(Path.Combine(AppData.DirectoryPath, "native-plugins"));
    private Action? refreshFolderPluginCards;

    private bool NativePluginManagerUsable => !uiReviewMode && !DeveloperLayoutPreview && !closed &&
        !exitRequested && !settingsDataBusy && !sessionEnding;
    private PluginRecord? FolderPluginRecord(string id) => FolderPluginCatalog.FindRecord(id);
    private bool FolderPluginRunning(string id) => folderThemePlugins.ContainsKey(id) ||
        folderPlugins.TryGetValue(id, out var session) && session.IsRunning;

    private void SaveFolderPluginChoice(PluginPackage package, bool enabled, string? fault = null)
    {
        FolderPluginCatalog.SaveChoice(package,enabled,fault);
    }

    private async Task<bool> EnableFolderPluginAsync(PluginPackage package)
    {
        var manifest = package.Manifest;
        string id = manifest.Id;
        _ = FolderPluginRecord(id); // Preserve corrupt/unknown preference files; do not start first and overwrite later.
        if (FolderPluginRunning(id)) return true;
        if (folderPlugins.ContainsKey(id)) await StopFolderPluginAsync(id);
        if (!NativePluginManagerUsable || currentPage != "plugins" || mini || trayHidden) return false;
        var approvals = new PluginApprovalStore(Path.Combine(AppData.DirectoryPath, "native-plugins"));
        if (manifest.Platform != null)
        {
            try { LoadFolderTheme(package, approvals); SaveFolderPluginChoice(package, true); }
            catch { if (folderThemePlugins.ContainsKey(id)) await StopFolderPluginAsync(id); throw; }
        }
        else
        {
            var approval = approvals.Load(package);
            if (approval == null)
            {
                var requested = manifest.Permissions.Where(p => p == PluginPermissions.Background || p.StartsWith("hardware.", StringComparison.Ordinal))
                    .Select(p => p switch
                    {
                        PluginPermissions.Background => "藏盘后继续运行", PluginPermissions.Performance => "设置性能档位",
                        PluginPermissions.FanBoost => "设置强冷", PluginPermissions.BatteryLimit => "设置充电上限（60%／80%／100%）", _ => p
                    }).ToArray();
                if (requested.Length > 0 && !await Confirm("启用插件", manifest.Name + " 申请以下能力：\n" + string.Join("\n", requested)))
                { SaveFolderPluginChoice(package, false); return false; }
                if (!NativePluginManagerUsable || currentPage != "plugins" || mini || trayHidden) return false;
                approval = PluginApproval.FromUserSelection(package, manifest.Permissions); approvals.Save(approval);
            }
            if (!NativePluginManagerUsable || currentPage != "plugins" || mini || trayHidden) return false;
            var runtime = EnsureNativePluginRuntime();
            var session = await runtime.EnableAsync(package, approval);
            if (!NativePluginManagerUsable || !ReferenceEquals(runtime, nativePluginRuntime) ||
                (!manifest.Background && (currentPage != "plugins" || mini || trayHidden)))
            { await runtime.DisableAsync(id); return false; }
            folderPlugins[id] = session; folderPluginDigests[id] = package.Digest;
            session.Stopped += reason =>
            {
                if (reason == null) return;
                DispatcherQueue.TryEnqueue(async () =>
                {
                    if (!ReferenceEquals(folderPlugins.GetValueOrDefault(id), session)) return;
                    try
                    {
                        FolderPluginCatalog.SetError(id,"插件已停止：" + reason);
                        FolderPluginCatalog.DisableInSession(id);
                        try { SaveFolderPluginChoice(package, false, reason); }
                        catch (Exception saveError) { DiagnosticLog.Record("plugin.fault_save_failed", new { type = saveError.GetType().Name }); }
                        await StopFolderPluginAsync(id);
                    }
                    catch (Exception error) { FolderPluginCatalog.SetError(id,error.Message); }
                    refreshFolderPluginCards?.Invoke();
                });
            };
            try
            {
                if (!session.IsRunning) throw new IOException("插件在启用期间停止。");
                SaveFolderPluginChoice(package, true);
            }
            catch { await StopFolderPluginAsync(id); throw; }
        }
        FolderPluginCatalog.MarkEnabled(id); return true;
    }

    private async Task DiscoverFolderPluginsAsync(bool enableNew)
    {
        var result = await Task.Run(new PluginDiscovery(AppContext.BaseDirectory).Refresh);
        if (!NativePluginManagerUsable || currentPage != "plugins" || mini || trayHidden) return;
        if (result.RootError != null) throw new IOException(result.RootError);
        foreach (string id in folderPlugins.Keys.Concat(folderThemePlugins.Keys).Distinct().ToArray())
            if (!result.Entries.Any(e => e.Package?.Manifest.Id == id && folderPluginDigests.GetValueOrDefault(id) == e.Package.Digest))
                await StopFolderPluginAsync(id);
        FolderPluginCatalog.AcceptDiscovery(result);
        if (!enableNew) return; // Initial discovery lists packages without granting or running them.
        foreach (var package in FolderPluginCatalog.Packages)
        {
            if (!NativePluginManagerUsable || currentPage != "plugins" || mini || trayHidden) break;
            string id = package.Manifest.Id;
            if (FolderPluginCatalog.DisabledThisSession(id))
            { if (FolderPluginRunning(id)) await StopFolderPluginAsync(id); continue; }
            var record = FolderPluginRecord(id);
            if (record is { Enabled: false } || record?.Fault != null)
            { if (FolderPluginRunning(id)) await StopFolderPluginAsync(id); continue; }
            try { await EnableFolderPluginAsync(package); }
            catch (Exception error)
            {
                FolderPluginCatalog.DisableInSession(id);
                FolderPluginCatalog.SetError(id,error.Message);
                try { SaveFolderPluginChoice(package, false, error.GetType().Name); }
                catch (Exception saveError) { DiagnosticLog.Record("plugin.choice_save_failed", new { type = saveError.GetType().Name }); }
            }
        }
    }
}
