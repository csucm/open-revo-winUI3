using System.Text.Json.Serialization;

namespace OpenRevo.ResidentProtocol;

public enum PanelApplicationAction
{
    CheckUpdate = 1, DownloadUpdate, CancelUpdate, OpenUpdateDirectory,
    ReadStartup, SetStartup, ShowTrayMenu
}

public sealed record PanelApplicationRequest(PanelApplicationAction Action, bool? Enabled = null)
{
    [JsonIgnore] public bool Valid => Enum.IsDefined(Action) && (Action == PanelApplicationAction.SetStartup) == Enabled.HasValue;
}

public enum PanelUpdatePhase { Idle, Checking, Current, Available, Downloading, Verified, Cancelled, Failed }

public sealed record PanelUpdateSnapshot(string CurrentVersion, bool CanInstall, PanelUpdatePhase Phase,
    string? AvailableVersion = null, long? Size = null, int? Percent = null, string? Message = null)
{
    [JsonIgnore] public bool Busy => Phase is PanelUpdatePhase.Checking or PanelUpdatePhase.Downloading or PanelUpdatePhase.Verified;
}

public sealed record PanelApplicationReply(string? Directory = null, bool StartupAvailable = false, bool? StartupEnabled = null);
