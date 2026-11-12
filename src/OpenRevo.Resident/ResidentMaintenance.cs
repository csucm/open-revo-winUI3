using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenRevo.Core;
using OpenRevo.Core.Hal;
using OpenRevo.ResidentProtocol;

namespace OpenRevo.Resident;

internal sealed partial class ResidentTray
{
    private readonly SemaphoreSlim maintenanceGate = new(1, 1);
    private readonly bool debugAvailable;
    private int logLevel;
    private string logFilter = "";
    private volatile bool dataExitRequired;

    private PanelMaintenanceSnapshot ReadMaintenanceSettings() =>
        new(AppData.DirectoryPath, AppData.DiagnosticsPath,
            installationDirectory == null ? AppData.ResidentStorageRoot : Path.GetDirectoryName(AppData.DefaultDirectoryPath)!,
            debugAvailable, logLevel, logFilter, AllowLocalDestinations: installationDirectory != null);

    private async Task<PanelFrame> RunMaintenanceRequestAsync(long id, PanelMaintenanceRequest request)
    {
        PanelFrame Reply(PanelOutcome outcome, PanelMaintenanceReply? maintenance = null, string? message = null) =>
            new(PanelProtocol.Revision, PanelFrameKind.Result, id, Outcome: outcome, Message: message, Maintenance: maintenance);
        await maintenanceGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (stopping || dataExitRequired) return Reply(PanelOutcome.Busy, message: "后台正在退出。");
            switch (request.Action)
            {
                case PanelMaintenanceAction.Read:
                    return Reply(PanelOutcome.Applied, new(ReadMaintenanceSettings()));
                case PanelMaintenanceAction.OpenData:
                case PanelMaintenanceAction.OpenDiagnostics:
                case PanelMaintenanceAction.PrepareMigration:
                    var settings = ReadMaintenanceSettings();
                    string directory = RunStorageAsDesktopUser(() =>
                    {
                        string path = UserDataStore.ValidateDirectory(request.Action switch
                        {
                            PanelMaintenanceAction.OpenData => settings.DataDirectory,
                            PanelMaintenanceAction.OpenDiagnostics => settings.DiagnosticsDirectory,
                            _ => settings.MigrationRoot
                        });
                        Directory.CreateDirectory(path);
                        return path;
                    });
                    return Reply(PanelOutcome.Applied, new(settings, directory));
                case PanelMaintenanceAction.ReadReport:
                    string report = await BuildResidentDiagnosticReportAsync().ConfigureAwait(false);
                    return Reply(PanelOutcome.Applied, new(Report: report));
                case PanelMaintenanceAction.SetLogging:
                    if (!debugAvailable) return Reply(PanelOutcome.Rejected, message: "此会话未启用额外调试日志选项。");
                    int level = request.LogLevel!.Value;
                    string filter = request.LogFilter!;
                    try { SystemPreferences.SetResidentLogging(level, filter); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        return Reply(PanelOutcome.ConfigurationSaveFailed, message: error.Message);
                    }
                    logLevel = level;
                    logFilter = filter;
                    try { await ConfigureLoggingAsync(level, filter).ConfigureAwait(false); }
                    catch (Exception error)
                    {
                        DiagnosticLog.Failure("resident.logging.apply_failed", error);
                        return Reply(PanelOutcome.ConfigurationSaved, new(ReadMaintenanceSettings()),
                            "设置已保存。Core 调试日志本次未确认。");
                    }
                    return Reply(PanelOutcome.ConfigurationSaved, new(ReadMaintenanceSettings()));
                case PanelMaintenanceAction.Migrate:
                case PanelMaintenanceAction.Reset:
                    if (updates.Snapshot.Busy)
                        return Reply(PanelOutcome.Busy, message: "请先完成或取消更新操作。");
                    string? destination = RunStorageAsDesktopUser(() =>
                    {
                        string? path = request.Destination == null ? null : installationDirectory == null
                            ? AppData.ValidateResidentStorageDestination(request.Destination)
                            : UserDataStore.ValidateInstalledDataLocation(request.Destination, installationDirectory);
                        if (path != null && Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
                            throw new IOException("迁移目标必须为空。");
                        UserDataStore.ValidateManaged(AppData.DirectoryPath);
                        return path;
                    });
                    var result = await ChangeDataStorageAsync(destination, request.Compact).ConfigureAwait(false);
                    return Reply(result.Outcome, new(Directory: result.Directory, ExitRequired: dataExitRequired), result.Message);
                default:
                    return Reply(PanelOutcome.Rejected);
            }
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or JsonException or Win32Exception)
        {
            DiagnosticLog.Failure("resident.maintenance.request_failed", error, new { request.Action });
            return Reply(PanelOutcome.Rejected, message: error.Message);
        }
        finally { maintenanceGate.Release(); }
    }

    private async Task<(PanelOutcome Outcome, string? Directory, string? Message)> ChangeDataStorageAsync(string? destination, bool? compact)
    {
        bool displayPaused = false, pluginsStopped = false, servicesStopping = false, writing = false;
        var journal = DiagnosticLog.Current;
        runtime.Operations.StopAccepting();
        try
        {
            displayPaused = await runtime.Display.TryPauseAsync().ConfigureAwait(false);
            if (!displayPaused) return (PanelOutcome.Busy, null, "请先完成刷新率试切。");
            runtime.PerformanceInput.SynchronizePhysicalMode(false);
            pluginsStopped = true;
            await plugins.StopAllAsync().ConfigureAwait(false);
            servicesStopping = true;
            await runtime.StopDesktopServicesAsync().ConfigureAwait(false);
            if (stopping) return (PanelOutcome.Busy, null, "后台正在退出。");
            if (compact.HasValue) panelSettings.RememberPanelShape(compact.Value);
            journal.SetDiskWrites(false);
            writing = true;
            string directory = await Task.Run(() => CommitStorageChange(destination)).ConfigureAwait(false);
            dataExitRequired = true;
            return (PanelOutcome.ConfigurationSaved, directory, null);
        }
        catch (Exception error)
        {
            if (writing)
            {
                // If reset could not restore its source, the backup is intact
                // but this process must not write to the now-invalid path.
                try { UserDataStore.ValidateManaged(AppData.DirectoryPath); }
                catch { dataExitRequired = true; }
            }
            if (!dataExitRequired) journal.SetDiskWrites(true);
            DiagnosticLog.Failure("resident.maintenance.storage_failed", error);
            return (dataExitRequired ? PanelOutcome.Unknown : PanelOutcome.ConfigurationSaveFailed, null, error.Message);
        }
        finally
        {
            if (!dataExitRequired && !stopping)
            {
                if (displayPaused) runtime.Display.Resume();
                runtime.Operations.Resume();
                if (displayPaused) runtime.PerformanceInput.SynchronizePhysicalMode(true);
                if (servicesStopping)
                {
                    try
                    {
                        await runtime.Desktop.RestoreSavedColorAsync().ConfigureAwait(false);
                        if (debugAvailable) await ConfigureLoggingAsync(logLevel, logFilter).ConfigureAwait(false);
                    }
                    catch (Exception error) { DiagnosticLog.Failure("resident.maintenance.services_restore_failed", error); }
                }
                if (pluginsStopped)
                {
                    try { await plugins.LoadApprovedAsync().ConfigureAwait(false); }
                    catch (Exception error) { DiagnosticLog.Failure("resident.maintenance.plugins_restore_failed", error); }
                }
                Volatile.Read(ref panel)?.SignalSnapshot();
            }
        }
    }

    private T RunStorageAsDesktopUser<T>(Func<T> work)
    {
        if (installationDirectory == null) return work();
        using var token = ResidentPanelLaunch.OpenDesktopToken();
        return WindowsIdentity.RunImpersonated(token, work);
    }

    private string CommitStorageChange(string? destination)
    {
        string source = AppData.DirectoryPath;
        if (installationDirectory == null)
            return destination == null ? UserDataStore.Reset(source) : UserDataStore.Migrate(source, destination, AppData.LocationPointer);

        if (destination != null)
            return RunStorageAsDesktopUser(() => UserDataStore.MigrateInstalled(source, destination, installationDirectory));
        if (source.Equals(Path.Combine(installationDirectory, "data"), StringComparison.OrdinalIgnoreCase))
            return UserDataStore.ResetInstalledPortable(installationDirectory);
        return RunStorageAsDesktopUser(() =>
            UserDataStore.Reset(UserDataStore.ValidateInstalledDataLocation(source, installationDirectory)));
    }

    private async Task ConfigureLoggingAsync(int level, string filter)
    {
        if (!await runtime.Operations.EnterAsync().ConfigureAwait(false)) throw new IOException("后台正在维护或退出。");
        try
        {
            if (runtime.Bridge is { } bridge)
                await Task.Run(() => bridge.ExecuteFeature(9, new { level, filter })).ConfigureAwait(false);
            DiagnosticLog.Record("resident.logging.configured", new { level, filterConfigured = filter.Length != 0 });
        }
        finally { runtime.Operations.Release(); }
    }

    private async Task<string> BuildResidentDiagnosticReportAsync()
    {
        if (!await runtime.Operations.EnterAsync().ConfigureAwait(false)) throw new IOException("后台正在维护或退出。");
        object state;
        try
        {
            state = await Task.Run(() => new
            {
                diagnosticsSchema = 4,
                exportedAt = DateTimeOffset.UtcNow,
                host = "OpenRevo.Resident",
                protocol = PanelProtocol.Revision,
                assemblyVersion = typeof(ResidentTray).Assembly.GetName().Version?.ToString(),
                coreModule = typeof(ApplicationHardwareRuntime).Module.ModuleVersionId,
                modelDeclaration = TryDiagnostic(() => runtime.Bridge?.Read(23)),
                device = TryDiagnostic(DeviceIdentity.Read),
                biosVersion = TryDiagnostic(DeviceIdentity.ReadBiosVersion),
                admission = CoreMachine.Current,
                panel = panelCommands.Read() with { Theme = null },
                process = ProcessResources.Read(),
                control = runtime.Hardware.State,
                performance = runtime.Hardware.PerformanceDiagnostics,
                physicalModeKey = runtime.PerformanceInput.Status,
                lighting = runtime.Hardware.LightingDiagnostics,
                gpu = TryDiagnostic(() => runtime.Telemetry.ResidentGpuTelemetryDiagnostics),
                color = TryDiagnostic(() => runtime.Bridge?.ReadColorService().Diagnostics),
                display = DisplayTopology.LastSnapshot,
                startup = StartupRegistration.LastInspection,
                desktopPreferences = DesktopServicePreferences.Load(),
                logging = new { level = logLevel, filter = logFilter },
                logs = DiagnosticLog.Collect(AppData.DiagnosticsPath, includeSharedTemp: false)
            }).ConfigureAwait(false);
        }
        finally { runtime.Operations.Release(); }
        var json = JsonSerializer.SerializeToNode(state) ?? throw new IOException("后台诊断报告为空。");
        ScrubReport(json);
        string report = json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (JsonSerializer.SerializeToUtf8Bytes(report).Length > 7 * 1024 * 1024)
            throw new IOException("诊断报告超过传输上限。");
        return report;
    }

    private static object? TryDiagnostic(Func<object?> read)
    {
        try { return read(); }
        catch (Exception error) { return new { error = error.Message, error.HResult }; }
    }

    private static void ScrubReport(JsonNode node)
    {
        if (node is JsonObject fields)
        {
            foreach (string key in fields.Select(field => field.Key).ToArray())
            {
                if (key.Contains("token", StringComparison.OrdinalIgnoreCase) || key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("password", StringComparison.OrdinalIgnoreCase) || key.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("authorization", StringComparison.OrdinalIgnoreCase) || key.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("serial", StringComparison.OrdinalIgnoreCase) || key.Equals("serialNumber", StringComparison.OrdinalIgnoreCase))
                    fields[key] = "<redacted>";
                else if (fields[key] is JsonValue value && value.TryGetValue<string>(out var text))
                    fields[key] = DiagnosticJournal.Scrub(text, 128 * 1024);
                else if (fields[key] is { } child) ScrubReport(child);
            }
        }
        else if (node is JsonArray items)
        {
            for (int i = 0; i < items.Count; i++)
                if (items[i] is JsonValue value && value.TryGetValue<string>(out var text))
                    items[i] = DiagnosticJournal.Scrub(text, 128 * 1024);
                else if (items[i] is { } child) ScrubReport(child);
        }
    }
}
