using System.Diagnostics;
using OpenRevo.ResidentProtocol;

namespace OpenRevo.Native;

public sealed partial class MainWindow
{
    private PanelMaintenanceSnapshot? residentMaintenance;
    private bool residentMaintenanceReading;
    private bool PaintDebugSettingsVisible => residentClient == null ? debugSettingsVisible : residentMaintenance?.DebugAvailable == true;
    private int PaintLogLevel => residentClient == null ? systemPreferences.LogLevel : residentMaintenance?.LogLevel ?? 0;
    private string PaintLogFilter => residentClient == null ? systemPreferences.LogFilter : residentMaintenance?.LogFilter ?? "";

    private async Task<PanelFrame> SendMaintenanceAsync(PanelMaintenanceRequest request, bool backgroundRead = false)
    {
        var reply = await SendResidentAsync(PanelOperation.Maintenance, backgroundRead: backgroundRead, maintenance: request);
        if (reply.Maintenance?.Settings is { } settings) residentMaintenance = settings;
        return reply;
    }

    private async Task ReadResidentMaintenanceSettingsAsync()
    {
        if (residentMaintenance != null || residentMaintenanceReading || closed || residentDetaching ||
            residentSnapshot?.Ready != true) return;
        residentMaintenanceReading = true;
        try
        {
            RequireResidentResult(await SendMaintenanceAsync(new(PanelMaintenanceAction.Read), backgroundRead: true), PanelOutcome.Applied);
            if (residentMaintenance?.DebugAvailable == true && PaintRoute == "diagnostics") QueueResidentRender();
            else RefreshPaintedState();
        }
        catch (Exception error) { if (!closed) actions.Add("后台存储状态：" + error.Message); }
        finally { residentMaintenanceReading = false; }
    }

    private async Task OpenResidentMaintenanceFolderAsync(bool diagnostics)
    {
        var reply = await SendMaintenanceAsync(new(diagnostics ? PanelMaintenanceAction.OpenDiagnostics : PanelMaintenanceAction.OpenData));
        RequireResidentResult(reply, PanelOutcome.Applied);
        if (reply.Maintenance?.Directory is { } directory && !closed && !residentDetaching)
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    private async Task RunResidentStorageChangeAsync(bool reset)
    {
        if (settingsDataBusy || closed || residentDetaching || residentSnapshot?.Ready != true) return;
        settingsDataBusy = true;
        RefreshPaintedState();
        try
        {
            PanelMaintenanceRequest request;
            if (reset)
            {
                if (!await Confirm("清除本版用户数据？", "将本版配置、方案和诊断移至备份目录，恢复空目录后退出程序。")) return;
                request = new(PanelMaintenanceAction.Reset, Confirmed: true);
            }
            else
            {
                var preparation = await SendMaintenanceAsync(new(PanelMaintenanceAction.PrepareMigration));
                RequireResidentResult(preparation, PanelOutcome.Applied);
                string rootDirectory = preparation.Maintenance?.Directory ?? throw new IOException("后台未提供迁移范围。");
                var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(AppWindow.Id) { SuggestedStartFolder = rootDirectory };
                var folder = await picker.PickSingleFolderAsync();
                if (folder == null || closed) return;
                string destination = Path.Combine(folder.Path, "OpenRevoNative");
                string restriction = preparation.Maintenance?.Settings?.AllowLocalDestinations == true
                    ? "请使用安装目录以外的本机磁盘。"
                    : "隔离候选的目标须位于 " + rootDirectory + " 内。";
                if (!await Confirm("迁移数据存储？", "将完整复制并校验到：" + destination +
                    "。" + restriction + "原目录保留，重启后使用新目录。")) return;
                request = new(PanelMaintenanceAction.Migrate, destination, Confirmed: true, Compact: mini);
            }
            if (closed || residentDetaching) return;
            var reply = await SendMaintenanceAsync(request);
            dataStorageExitPending = reply.Maintenance?.ExitRequired == true;
            if (reply.Outcome != PanelOutcome.ConfigurationSaved)
            {
                await ShowMessage(reset ? "数据未清除" : "迁移未完成", reply.Message ?? "后台未确认保存。");
                return;
            }
            string path = reply.Maintenance?.Directory ?? "未知";
            if (reset) await ShowMessage("数据已备份并清空", "原数据保留在：" + path + "。关闭提示后退出程序。");
            else await ShowMessage("数据已复制并校验", "原目录保留。关闭提示后退出程序，请重新打开以使用新目录：" + path);
        }
        finally
        {
            settingsDataBusy = false;
            if (dataStorageExitPending && !closed)
            {
                try { RequireResidentResult(await SendResidentAsync(PanelOperation.ExitResident), PanelOutcome.Applied); }
                catch (IOException error) { actions.Add("维护后退出连接：" + error.Message); }
                finally { CloseResidentPanel(); }
            }
            if (!closed) RefreshPaintedState();
        }
    }

    private async Task ExportResidentDiagnosticAsync()
    {
        if (diagnosticExportBusy || closed || residentDetaching) return;
        diagnosticExportBusy = true;
        string? temporary = null;
        try
        {
            var directory = await SendMaintenanceAsync(new(PanelMaintenanceAction.OpenDiagnostics));
            RequireResidentResult(directory, PanelOutcome.Applied);
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(AppWindow.Id)
            {
                SuggestedFileName = $"report-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}",
                SuggestedStartFolder = directory.Maintenance?.Directory ?? ""
            };
            picker.FileTypeChoices.Add("诊断报告 JSON", [".json"]);
            var selected = await picker.PickSaveFileAsync();
            if (selected == null || closed || residentDetaching) return;
            var reply = await SendMaintenanceAsync(new(PanelMaintenanceAction.ReadReport));
            RequireResidentResult(reply, PanelOutcome.Applied);
            if (closed || residentDetaching) return;
            string report = reply.Maintenance?.Report ?? throw new IOException("后台未返回报告内容。");
            temporary = selected.Path + "-" + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporary, report);
            File.Move(temporary, selected.Path, overwrite: true);
            temporary = null;
            diagnosticExportLastPath = selected.Path;
            if (await PaintDialog("诊断报告已保存", Paint.Text(selected.Path), "打开所在文件夹", "完成") &&
                Path.GetDirectoryName(selected.Path) is { } containingFolder)
                Process.Start(new ProcessStartInfo(containingFolder) { UseShellExecute = true });
        }
        finally
        {
            diagnosticExportBusy = false;
            if (temporary != null)
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { actions.Add("报告临时文件：" + error.Message); }
            }
        }
    }

    private async Task SavePaintLoggingAsync(int level, string filter)
    {
        if (residentClient == null)
        {
            await SaveSystemSettings(systemPreferences with { LogLevel = level, LogFilter = filter });
            await ConfigureDebugLog();
            return;
        }
        var reply = await SendMaintenanceAsync(new(PanelMaintenanceAction.SetLogging, LogLevel: level, LogFilter: filter));
        RequireResidentResult(reply, PanelOutcome.ConfigurationSaved);
        if (reply.Message != null) await ShowMessage("调试日志", reply.Message);
    }
}
