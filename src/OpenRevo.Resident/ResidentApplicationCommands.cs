using OpenRevo.Core;
using OpenRevo.ResidentProtocol;

namespace OpenRevo.Resident;

internal sealed partial class ResidentTray
{
    private readonly string? installationDirectory;
    private readonly ResidentUpdates updates;

    private async Task<PanelFrame> RunApplicationRequestAsync(long id, PanelApplicationRequest request)
    {
        PanelFrame Reply(PanelOutcome outcome, PanelApplicationReply? value = null, string? message = null) =>
            new(PanelProtocol.Revision, PanelFrameKind.Result, id, panelCommands.Read(), outcome, message, Application: value);
        await maintenanceGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (stopping || dataExitRequired) return Reply(PanelOutcome.Busy);
            switch (request.Action)
            {
                case PanelApplicationAction.CheckUpdate: return Reply(updates.Check());
                case PanelApplicationAction.DownloadUpdate:
                    return Reply(updates.Download(), message: installationDirectory == null
                        ? "自动安装更新需要有效的安装登记。" : null);
                case PanelApplicationAction.CancelUpdate: return Reply(updates.Cancel());
                case PanelApplicationAction.OpenUpdateDirectory:
                    return Reply(PanelOutcome.Applied, new(Directory: await Task.Run(updates.OpenDirectory).ConfigureAwait(false)));
                case PanelApplicationAction.ShowTrayMenu:
                    return Reply(PostMessage(window, PresentTrayMenu, 0, 0) ? PanelOutcome.CommandSent : PanelOutcome.Unknown);
                case PanelApplicationAction.ReadStartup:
                case PanelApplicationAction.SetStartup:
                    if (installationDirectory == null)
                        return request.Action == PanelApplicationAction.ReadStartup
                            ? Reply(PanelOutcome.Applied, new())
                            : Reply(PanelOutcome.Rejected, message: "当前没有候选后台的安装自启登记。");
                    bool enabled = await Task.Run(() => request.Action == PanelApplicationAction.ReadStartup
                        ? StartupRegistration.ReadResidentLogon(installationDirectory)
                        : StartupRegistration.SetResidentLogon(installationDirectory, request.Enabled!.Value)).ConfigureAwait(false);
                    return Reply(request.Action == PanelApplicationAction.ReadStartup ? PanelOutcome.Applied : PanelOutcome.ReadbackConfirmed,
                        new(StartupAvailable: true, StartupEnabled: enabled));
                default: return Reply(PanelOutcome.Rejected);
            }
        }
        finally { maintenanceGate.Release(); }
    }
}
