using System.Diagnostics;
using OpenRevo.ResidentProtocol;

namespace OpenRevo.Native;

public sealed partial class MainWindow
{
    private bool residentStartupRead, residentStartupReading;
    private bool residentStartupAvailable;
    private PanelUpdateSnapshot? ResidentUpdate => residentSnapshot?.Update;
    private bool PaintUpdateBusy => residentClient == null ? updateBusy : ResidentUpdate?.Busy == true;
    private bool ResidentUpdateRequestBusy => PanelActionBusy(PanelOperation.Application, PanelApplicationAction.CheckUpdate) ||
        PanelActionBusy(PanelOperation.Application, PanelApplicationAction.DownloadUpdate);
    private string PaintUpdateMessage => residentClient == null ? updateMessage : ResidentUpdate switch
    {
        { Phase: PanelUpdatePhase.Checking } => "正在检查社区版更新…",
        { Phase: PanelUpdatePhase.Current } => "已是最新社区版。",
        { Phase: PanelUpdatePhase.Available, CanInstall: true } found =>
            $"发现 {found.AvailableVersion} · {found.Size / 1048576.0:F1} MiB，下载并校验后退出安装。",
        { Phase: PanelUpdatePhase.Available } found =>
            $"发现 {found.AvailableVersion} · {found.Size / 1048576.0:F1} MiB。源码候选可打开发布页查看。",
        { Phase: PanelUpdatePhase.Downloading } transfer => $"正在下载 {transfer.AvailableVersion} · {transfer.Percent}%",
        { Phase: PanelUpdatePhase.Verified } => "校验通过，正在释放后台资源。",
        { Phase: PanelUpdatePhase.Cancelled } => "更新已取消。",
        { Phase: PanelUpdatePhase.Failed } failed => "更新未完成：" + failed.Message,
        _ => "检查社区版发布。"
    };

    private Task<PanelFrame> SendResidentApplicationAsync(PanelApplicationAction action, bool? enabled = null, bool backgroundRead = false) =>
        SendResidentAsync(PanelOperation.Application, backgroundRead: backgroundRead, application: new(action, enabled));

    private async Task RunResidentUpdateActionAsync(PanelApplicationAction action)
    {
        var reply = await SendResidentApplicationAsync(action);
        if (reply.Outcome is not (PanelOutcome.CommandSent or PanelOutcome.Applied))
            await ShowMessage("更新未完成", reply.Message ?? "后台没有受理此操作。");
        if (reply.Outcome == PanelOutcome.Applied && reply.Application?.Directory is { } directory && !closed)
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    private async Task ReadResidentStartupAsync()
    {
        if (residentStartupRead || residentStartupReading || closed || residentDetaching || residentSnapshot?.Ready != true) return;
        residentStartupReading = true;
        try
        {
            var result = await SendResidentApplicationAsync(PanelApplicationAction.ReadStartup, backgroundRead: true);
            RequireResidentResult(result, PanelOutcome.Applied);
            residentStartupAvailable = result.Application?.StartupAvailable == true;
            paintStartup = result.Application?.StartupEnabled;
            residentStartupRead = true;
            RefreshPaintedState();
        }
        catch (Exception error) { if (!closed) actions.Add("后台自启状态：" + error.Message); }
        finally { residentStartupReading = false; }
    }

    private async Task SetResidentStartupAsync(bool enabled)
    {
        try
        {
            var result = await SendResidentApplicationAsync(PanelApplicationAction.SetStartup, enabled);
            RequireResidentResult(result, PanelOutcome.ReadbackConfirmed);
            paintStartup = result.Application?.StartupEnabled;
        }
        catch
        {
            paintStartup = null;
            residentStartupRead = false;
            _ = ReadResidentStartupAsync();
            throw;
        }
    }

    private async Task SetResidentOsdAppearanceAsync(Func<PanelOsdAppearance, PanelOsdAppearance> update)
    {
        if (residentSnapshot == null) return;
        var result = await SendResidentAsync(PanelOperation.SetOsdAppearance,
            osdAppearance: update(residentSnapshot.Osd.Appearance));
        RequireResidentResult(result, PanelOutcome.Applied);
    }

    private PaintSelect ResidentPowerPlanChoice(string label)
    {
        bool Busy() => PanelActionBusy(PanelOperation.Desktop, typeof(PanelSelectPowerPlan));
        PanelPowerPlan[] presented = [];
        var choice = new PaintSelect(label, [], -1, async index =>
        {
            if (Busy() || residentPower?.ActivePlan is not Guid active || index < 0 || index >= presented.Length ||
                active == presented[index].Id) return;
            try
            {
                await RunResidentActionAsync(PanelOperation.Desktop, desktop: new PanelSelectPowerPlan(presented[index].Id, active));
            }
            catch (Exception error) { if (!closed) await ShowMessage("电源方案切换未完成", error.Message); }
            finally { RefreshPaintedState(); }
        }) { Width = 180 };
        paintBindings.Add(() =>
        {
            var plans = residentPower?.Plans ?? [];
            bool busy = Busy();
            if (!presented.SequenceEqual(plans)) choice.DismissPopup();
            presented = plans;
            choice.Replace(plans.Select(plan => plan.Name).ToArray(), busy ? choice.SelectedIndex :
                Array.FindIndex(plans, plan => plan.Id == residentPower?.ActivePlan));
            choice.IsEnabled = residentPower?.ActivePlan.HasValue == true && !busy;
        });
        return choice;
    }
}
