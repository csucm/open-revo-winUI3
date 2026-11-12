using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.Core;
using OpenRevo.PluginHost;
using OpenRevo.PluginSdk;

namespace OpenRevo.Native;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, PluginSession> folderPlugins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> folderPluginDigests = new(StringComparer.Ordinal);
    private bool loadingPluginFolder;

    private async void StopForegroundFolderPlugins()
    {
        foreach (var session in folderPlugins.Values.Where(s => !s.Manifest.Background && s.IsRunning).ToArray())
        {
            try { await (nativePluginRuntime?.DisableAsync(session.Manifest.Id) ?? Task.CompletedTask); }
            catch (Exception error) { DiagnosticLog.Record("plugin.foreground_stop_failed", new { type = error.GetType().Name }); }
        }
    }

    private void RenderNativePluginFolders()
    {
        var status = Paint.Text("将插件子文件夹放入 plugins，点击刷新载入。", 12, muted: true);
        var views = Paint.Stack(8);
        var load = new PaintButton("刷新");
        var open = new PaintButton("打开插件目录", () =>
        {
            try
            {
                var discovery = new PluginDiscovery(AppContext.BaseDirectory);
                var result = discovery.Refresh();
                if (result.RootError != null) { status.Text = result.RootError; return; }
                OpenLocalFolder(discovery.Root);
            }
            catch (Exception error) { status.Text = "无法打开插件目录：" + error.Message; }
        });
        bool Enabled() => NativePluginManagerUsable && !loadingPluginFolder;
        void ShowFunctions()
        {
            views.Children.Clear();
            open.IsEnabled = load.IsEnabled = Enabled();
            var packages=FolderPluginCatalog.Packages;
            var discoveryErrors=FolderPluginCatalog.DiscoveryErrors;
            foreach (var package in packages)
            {
                var manifest = package.Manifest;
                string id = manifest.Id;
                bool running = FolderPluginRunning(id);
                folderPlugins.TryGetValue(id, out var session);
                bool theme = manifest.Platform?.Contributions.Theme.Length > 0;
                string description = manifest.Description ?? (theme ? "调整原生窗口配色，关闭后恢复原配色。" : "使用软件提供的原生组件扩展功能。");
                string state = running ? (theme ? "已启用 · 配色已应用" : "已启用 · 沙箱运行") : "已关闭 · 可随时重新启用";
                bool stateReadable = true;
                try
                {
                    if (!running && FolderPluginRecord(id)?.Fault is { } fault) state = "已停用 · " + fault;
                }
                catch (Exception error) { state = "启停设置读取失败：" + error.Message; stateReadable = false; }
                if (FolderPluginCatalog.Error(id) is { } errorText) state = errorText;
                FrameworkElement? content = null;
                if (!theme)
                {
                    if (session?.IsRunning == true) content = CreateNativePluginSurface(session, showHeading: false);
                    else if ((session?.View ?? FolderPluginCatalog.View(id)) is { } view)
                    {
                        var saved = new NativePluginViewPresenter(manifest.Name, showHeading: false);
                        saved.Apply(view, false, null); content = saved;
                    }
                }
                var info = new NativePluginCardInfo(id, manifest.Name, manifest.Version, theme ? "窗口主题" : "原生插件",
                    description, Path.GetFileName(package.DirectoryPath), manifest.Author, running, state);
                views.Children.Add(NativePluginCards.Create(info, Enabled() && (stateReadable || running), desired => _ = ToggleAsync(package, desired), content));
            }
            foreach (var invalid in discoveryErrors)
                views.Children.Add(Paint.Card(Paint.Stack(6, Paint.Text(invalid.FolderName, 14, true),
                    Paint.Text("无法载入：" + invalid.Error, 12, muted: true)), 14, 12));
            if (packages.Length == 0 && discoveryErrors.Length == 0)
            {
                var empty = Paint.Stack(8, Paint.Text("暂未发现自定义插件", 14, true),
                    Paint.Text("将包含 plugin.json 的插件子文件夹放入 plugins，再点击右上角“刷新”。", 12, muted: true));
                empty.Margin = new(0, 18, 0, 18);
                views.Children.Add(empty);
            }
        }
        async Task ToggleAsync(PluginPackage package, bool desired)
        {
            if (!Enabled()) { ShowFunctions(); return; }
            loadingPluginFolder = true; ShowFunctions();
            string id = package.Manifest.Id;
            try
            {
                if (desired)
                {
                    // Re-read at every explicit enable so a stale card cannot approve a changed package.
                    var current = await Task.Run(() => PluginPackage.Read(package.DirectoryPath));
                    if (current.Manifest.Id != id || current.Digest != package.Digest)
                        throw new IOException("插件文件已变化，请刷新列表后重新启用。");
                    if (await EnableFolderPluginAsync(current)) status.Text = "已启用 " + current.Manifest.Name + "。";
                    else status.Text = "插件未启用。";
                }
                else
                {
                    FolderPluginCatalog.DisableInSession(id);
                    Exception? saveError = null;
                    try { SaveFolderPluginChoice(package, false); } catch (Exception error) { saveError = error; }
                    await StopFolderPluginAsync(id);
                    if (saveError == null)
                    { FolderPluginCatalog.ClearError(id); status.Text = "已关闭，插件卡片和设置已保留。"; }
                    else
                    {
                        FolderPluginCatalog.SetError(id,"已关闭，但开关设置未能保存：" + saveError.Message);
                        status.Text = FolderPluginCatalog.Error(id)!;
                    }
                }
            }
            catch (Exception error)
            {
                if (desired && !FolderPluginRunning(id)) FolderPluginCatalog.DisableInSession(id);
                FolderPluginCatalog.SetError(id,"操作未完成：" + error.Message);
                status.Text = FolderPluginCatalog.Error(id)!;
            }
            finally { loadingPluginFolder = false; ShowFunctions(); }
        }
        async Task RefreshAsync(bool enableNew)
        {
            if (!Enabled()) return;
            loadingPluginFolder = true; ShowFunctions();
            try
            {
                await DiscoverFolderPluginsAsync(enableNew);
                status.Text = enableNew ? "列表已刷新。关闭的插件保持关闭。" : "已发现本机插件，可通过右侧开关启用。";
            }
            catch (Exception error) { status.Text = "读取插件列表失败：" + error.Message; }
            finally { loadingPluginFolder = false; ShowFunctions(); }
        }
        load.Click += () => _ = RefreshAsync(enableNew: true);
        Action refresh = ShowFunctions;
        views.Loaded += async (_, _) =>
        {
            refreshFolderPluginCards = refresh;
            if (!FolderPluginCatalog.DiscoveryStarted) await RefreshAsync(enableNew: false);
        };
        views.Unloaded += (_, _) => { if (ReferenceEquals(refreshFolderPluginCards, refresh)) refreshFolderPluginCards = null; };
        ShowFunctions();
        page.Children.Add(NativePluginCards.Section(Paint.Inline(8, open, load), status, views));
    }
}
