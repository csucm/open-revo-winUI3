using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenRevo.PluginHost;
using OpenRevo.PluginSdk;
using OpenRevo.ResidentProtocol;

namespace OpenRevo.Native;

public sealed partial class MainWindow
{
    private PanelPluginEntry[] residentPluginEntries = [];
    private string[] residentPluginErrors = [];
    private readonly Dictionary<PanelPluginKey, PanelPluginPage> residentPluginViews = [];
    private readonly Dictionary<PanelPluginKey, NativePluginViewPresenter> residentPluginPresenters = [];
    private StackPanel? residentPluginCards;
    private TextBlock? residentPluginStatus;
    private bool residentPluginPageOpen, residentPluginSyncing, residentPluginCommand;
    private long residentThemeRevision = -1, residentPluginCatalogSeen = -1, residentPluginViewsSeen = -1;

    private bool ResidentPluginPageVisible =>
        initialized && !mini && !residentDetaching && !closed && PaintRoute == "plugins" && IsVisibleNow();

    private void ApplyResidentTheme(PanelThemeSnapshot? snapshot)
    {
        if (snapshot == null || snapshot.Revision <= residentThemeRevision) return;
        try
        {
            var palette = snapshot.Colors.Length == 0 ? PluginThemePalette.Empty : PluginThemePalette.FromColors(
                snapshot.Colors.Select(c => new PluginThemeValue(c.Surface, c.Mode, c.Token, c.Value)));
            NativePluginThemePalette.Apply(palette);
            residentThemeRevision = snapshot.Revision;
        }
        catch (Exception error)
        {
            actions.Add("后台主题绘制：" + error.Message);
            residentThemeRevision = snapshot.Revision;
        }
    }

    private void PresentResidentPluginNavigation(PanelPluginDestination destination)
    {
        if (!initialized || closed || residentDetaching || mini || dialogOpen || !IsVisibleNow()) return;
        string? page = destination switch
        {
            PanelPluginDestination.Overview => "overview", PanelPluginDestination.Display => "display",
            PanelPluginDestination.Power => "power", PanelPluginDestination.Devices => "devices",
            PanelPluginDestination.Settings => "settings", _ => null
        };
        if (page != null) PaintNavigate(page);
    }

    private void RenderResidentPlugins()
    {
        residentPluginCards = Paint.Stack(8);
        residentPluginStatus = Paint.Text("从常驻后台读取本机插件。", 12, muted: true);
        var refresh = PB("刷新", () => RunResidentPluginAsync(new(PanelPluginOperation.Refresh)));
        var open = PB("打开插件目录", () => RunResidentPluginAsync(new(PanelPluginOperation.OpenDirectory)));
        paintBindings.Add(() => refresh.IsEnabled = open.IsEnabled = residentSnapshot?.Ready == true && !residentPluginCommand);
        page.Children.Add(NativePluginCards.Section(Paint.Inline(8, open, refresh), residentPluginStatus, residentPluginCards));
        DrawResidentPluginCards();
    }

    private void DrawResidentPluginCards()
    {
        if (!ResidentPluginPageVisible || residentPluginCards == null) return;
        residentPluginCards.Children.Clear();
        residentPluginPresenters.Clear();
        foreach (var entry in residentPluginEntries)
        {
            FrameworkElement? content = null;
            if (!entry.Theme && (entry.Running || entry.HasView || residentPluginViews.ContainsKey(entry.Key)))
            {
                var view = new NativePluginViewPresenter(entry.Name, showHeading: false);
                view.Input += input => _ = RunResidentPluginAsync(new(PanelPluginOperation.Input, entry.Key,
                    ControlId: input.ControlId, Value: input.Value));
                residentPluginPresenters[entry.Key] = view;
                PresentResidentPluginView(entry, view);
                content = view;
            }
            if (entry.Granted != null)
            {
                var revoke = PB("撤销授权", async () =>
                {
                    if (await Confirm("撤销插件授权？", entry.Name + " 将停止运行，插件文件和设置会保留。"))
                        await RunResidentPluginAsync(new(PanelPluginOperation.Revoke, entry.Key));
                });
                revoke.IsEnabled = !residentPluginCommand;
                content = content == null ? revoke : Paint.Stack(6, content, revoke);
            }
            string status = entry.Fault ?? (entry.Running
                ? entry.Theme ? "已启用 · 配色已应用" : entry.Background ? "已启用 · 藏盘后继续运行" : "已启用 · 沙箱运行"
                : entry.Enabled ? "已保存启用 · 当前未运行" : entry.Supported ? "已关闭" : "此插件类型暂不可用");
            var info = new NativePluginCardInfo(entry.Key.Id, entry.Name, entry.Version, entry.Theme ? "窗口主题" : "原生插件",
                entry.Description ?? "", entry.Folder, entry.Author, entry.Running, status);
            residentPluginCards.Children.Add(NativePluginCards.Create(info, entry.Supported && residentPluginPageOpen && !residentPluginCommand,
                enabled => _ = ToggleResidentPluginAsync(entry, enabled), content));
        }
        foreach (string error in residentPluginErrors)
            residentPluginCards.Children.Add(Paint.Card(Paint.Text("无法载入：" + error, 12, muted: true), 14, 12));
        if (residentPluginPageOpen && residentPluginCards.Children.Count == 0)
            residentPluginCards.Children.Add(Paint.Text("暂未发现自定义插件。", 14, true));
    }

    private void PresentResidentPluginView(PanelPluginEntry entry, NativePluginViewPresenter presenter)
    {
        var page = residentPluginViews.GetValueOrDefault(entry.Key);
        PluginView? view = page?.Title is { } title ? new(title, page.Controls.Select(c => new PluginControl(
            c.Id, c.Kind, c.Label, c.Value, c.Enabled,
            c.Choices?.Select(choice => new PluginChoice(choice.Value, choice.Label)).ToArray())).ToArray()) : null;
        presenter.Apply(view, entry.Running && page?.Running == true && !residentPluginCommand, page?.Fault);
    }

    private void RefreshResidentPluginPresenters()
    {
        foreach (var entry in residentPluginEntries)
            if (residentPluginPresenters.TryGetValue(entry.Key, out var presenter))
                PresentResidentPluginView(entry, presenter);
    }

    private void ApplyResidentPluginReply(PanelPluginSnapshot? reply)
    {
        if (reply?.Entries is { } entries)
        {
            var keys = entries.Select(entry => entry.Key).ToHashSet();
            foreach (var key in residentPluginViews.Keys.Where(key => !keys.Contains(key)).ToArray())
                residentPluginViews.Remove(key);
            residentPluginEntries = entries;
            residentPluginErrors = reply.Errors ?? [];
            DrawResidentPluginCards();
        }
        if (reply?.Page is { } view)
        {
            residentPluginViews[view.Key] = view;
            if (residentPluginPresenters.TryGetValue(view.Key, out var presenter) &&
                residentPluginEntries.FirstOrDefault(e => e.Key == view.Key) is { } entry)
                PresentResidentPluginView(entry, presenter);
        }
    }

    private async Task ToggleResidentPluginAsync(PanelPluginEntry entry, bool enabled)
    {
        if (!ResidentPluginPageVisible || residentPluginCommand) return;
        if (!enabled)
        {
            await RunResidentPluginAsync(new(PanelPluginOperation.Disable, entry.Key));
            return;
        }
        if (entry.Granted != null)
        {
            await RunResidentPluginAsync(new(PanelPluginOperation.Enable, entry.Key));
            return;
        }
        var permissions = Paint.Stack(6, Paint.Text(entry.Name + " " + entry.Version + "\n包摘要 " + entry.Key.Digest, 12));
        var checks = new List<(string Permission, CheckBox Box)>();
        foreach (string permission in entry.Requested)
        {
            bool required = permission is PluginPermissions.Background or PluginPlatformPermissions.Theme;
            var box = new CheckBox { Content = ResidentPluginPermissionLabel(permission), IsChecked = true, IsEnabled = !required };
            checks.Add((permission, box));
            permissions.Children.Add(box);
        }
        try
        {
            var content = new ScrollViewer { MaxHeight = 330, Content = permissions };
            if (await PaintDialog("审核插件权限", content, "授权并启用", "取消") && ResidentPluginPageVisible)
                await RunResidentPluginAsync(new(PanelPluginOperation.Enable, entry.Key,
                    checks.Where(item => item.Box.IsChecked == true).Select(item => item.Permission).ToArray()));
        }
        catch (Exception error) { SetResidentPluginStatus(error.Message); }
        finally { DrawResidentPluginCards(); }
    }

    private async Task RunResidentPluginAsync(PanelPluginRequest request)
    {
        if (!ResidentPluginPageVisible || residentPluginCommand || residentSnapshot?.Ready != true) return;
        residentPluginCommand = true;
        if (request.Operation == PanelPluginOperation.Input) RefreshResidentPluginPresenters();
        else DrawResidentPluginCards();
        try
        {
            var result = await SendResidentAsync(PanelOperation.Plugins, plugin: request);
            ApplyResidentPluginReply(result.Plugins);
            if (request.Operation == PanelPluginOperation.OpenDirectory && result.Outcome == PanelOutcome.Applied &&
                result.Plugins?.Directory is { } directory)
                Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            if (request.Operation != PanelPluginOperation.Input || result.Outcome != PanelOutcome.CommandSent)
                SetResidentPluginStatus(result.Message ?? (result.Outcome is PanelOutcome.Applied or PanelOutcome.ConfigurationSaved
                    ? "插件状态已更新。" : "后台未确认操作。"));
        }
        catch (Exception error) { SetResidentPluginStatus(error.Message); }
        finally
        {
            residentPluginCommand = false;
            if (request.Operation == PanelPluginOperation.Input) RefreshResidentPluginPresenters();
            else DrawResidentPluginCards();
            RefreshPaintedState();
            SyncResidentPluginPage();
        }
    }

    private void SetResidentPluginStatus(string text)
    {
        if (ResidentPluginPageVisible && residentPluginStatus != null) residentPluginStatus.Text = text;
    }

    private async void SyncResidentPluginPage()
    {
        if (residentClient == null) return;
        if (mini || PaintRoute != "plugins")
        {
            residentPluginCards = null;
            residentPluginStatus = null;
            residentPluginPresenters.Clear();
        }
        if (residentPluginSyncing || residentPluginCommand || closed || residentDetaching ||
            residentSnapshot?.Ready != true) return;
        bool visible = ResidentPluginPageVisible;
        if (!visible && !residentPluginPageOpen) return;
        long catalogRevision = residentSnapshot.PluginCatalogRevision;
        long viewRevision = residentSnapshot.PluginRevision;
        if (visible && residentPluginPageOpen && catalogRevision == residentPluginCatalogSeen && viewRevision == residentPluginViewsSeen) return;
        residentPluginSyncing = true;
        bool continueSync = false;
        try
        {
            if (visible != residentPluginPageOpen)
            {
                var operation = visible ? PanelPluginOperation.OpenViews : PanelPluginOperation.CloseViews;
                var reply = await SendResidentAsync(PanelOperation.Plugins, backgroundRead: true, plugin: new(operation));
                RequireResidentResult(reply, PanelOutcome.Applied);
                residentPluginPageOpen = visible;
                ApplyResidentPluginReply(reply.Plugins);
                residentPluginCatalogSeen = catalogRevision;
                residentPluginViewsSeen = -1;
            }
            else if (catalogRevision != residentPluginCatalogSeen)
            {
                var reply = await SendResidentAsync(PanelOperation.Plugins, backgroundRead: true, plugin: new(PanelPluginOperation.ReadState));
                RequireResidentResult(reply, PanelOutcome.Applied);
                ApplyResidentPluginReply(reply.Plugins);
                residentPluginCatalogSeen = catalogRevision;
            }
            if (ResidentPluginPageVisible && residentPluginPageOpen && viewRevision != residentPluginViewsSeen)
            {
                foreach (var entry in residentPluginEntries.Where(e => !e.Theme &&
                    (e.Running || e.HasView && !residentPluginViews.ContainsKey(e.Key))).ToArray())
                {
                    if (!ResidentPluginPageVisible || residentPluginCommand) break;
                    var reply = await SendResidentAsync(PanelOperation.Plugins, backgroundRead: true,
                        plugin: new(PanelPluginOperation.ReadView, entry.Key));
                    ApplyResidentPluginReply(reply.Plugins);
                }
                if (!residentPluginCommand) residentPluginViewsSeen = viewRevision;
            }
            continueSync = true;
        }
        catch (Exception error) { SetResidentPluginStatus(error.Message); }
        finally
        {
            residentPluginSyncing = false;
            if (continueSync)
                DispatcherQueue.TryEnqueue(() => SyncResidentPluginPage());
        }
    }

    private static string ResidentPluginPermissionLabel(string permission) => permission switch
    {
        PluginPlatformPermissions.Theme => "应用静态窗口配色",
        PluginPermissions.Background => "藏盘后继续运行",
        PluginPermissions.SummaryRead => "读取系统摘要",
        PluginPermissions.PowerRead => "读取供电状态",
        PluginPermissions.Config => "保存插件自己的配置",
        PluginPermissions.View => "显示插件控件",
        PluginPermissions.Navigate => "在已打开面板中切换页面",
        PluginPermissions.Performance => "设置性能档位",
        PluginPermissions.FanBoost => "设置强冷",
        PluginPermissions.BatteryLimit => "设置充电上限",
        _ => permission
    };
}
