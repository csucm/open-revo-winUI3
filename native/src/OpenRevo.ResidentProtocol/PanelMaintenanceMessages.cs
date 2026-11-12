using System.Text.Json.Serialization;

namespace OpenRevo.ResidentProtocol;

public enum PanelMaintenanceAction { Read = 1, OpenData, OpenDiagnostics, PrepareMigration, Migrate, Reset, ReadReport, SetLogging }

public sealed record PanelMaintenanceRequest(PanelMaintenanceAction Action, string? Destination = null,
    bool Confirmed = false, bool? Compact = null, int? LogLevel = null, string? LogFilter = null)
{
    [JsonIgnore]
    public bool Valid
    {
        get
        {
            if (Action == PanelMaintenanceAction.SetLogging)
                return Destination == null && !Confirmed && Compact == null && LogLevel is >= 0 and <= 2 && ValidFilter(LogFilter);
            if (LogLevel != null || LogFilter != null) return false;
            return Action switch
            {
                PanelMaintenanceAction.Read or PanelMaintenanceAction.OpenData or PanelMaintenanceAction.OpenDiagnostics or
                    PanelMaintenanceAction.PrepareMigration or PanelMaintenanceAction.ReadReport =>
                    Destination == null && !Confirmed && Compact == null,
                PanelMaintenanceAction.Migrate => Confirmed && Compact.HasValue &&
                    Destination is { Length: > 0 and <= 1024 } && !Destination.Any(char.IsControl),
                PanelMaintenanceAction.Reset => Confirmed && Destination == null && Compact == null,
                _ => false
            };
        }
    }

    public static bool ValidFilter(string? filter) => filter is "" or "cooler" or "custom" or "light" or "power" or "ec" or "device" or "ui";
}

public sealed record PanelMaintenanceSnapshot(string DataDirectory, string DiagnosticsDirectory, string MigrationRoot,
    bool DebugAvailable, int LogLevel, string LogFilter, bool AllowLocalDestinations = false);
public sealed record PanelMaintenanceReply(PanelMaintenanceSnapshot? Settings = null, string? Directory = null,
    string? Report = null, bool ExitRequired = false);
