using System.Text.Json.Serialization;

namespace OpenRevo.ResidentProtocol;

public static class PanelProtocol
{
    public const int Revision = 15;
    public const int MaximumIccBytes = 4 * 1024 * 1024;
    public const int MaximumTuningPresetBytes = 1024 * 1024;
    public const string PipePrefix = "OpenRevo.Resident.Panel.";

    public static bool ValidPipeName(string name) => name.StartsWith(PipePrefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(name[PipePrefix.Length..], "N", out _);
}

public enum PanelOperation
{
    Takeover = 1, Release, SelectMode, SetBatteryLimit, ToggleFanBoost, ToggleKeepAwake, Detach, ReadGpu, SelectGpu,
    ReadSwitches, ToggleCamera, ToggleMicrophone, ToggleWinLock, ToggleWifi, ToggleBluetooth, ToggleTouchpad, ToggleFnLock,
    ReadDisplay, SetBrightness, BeginRefresh, KeepRefresh, RestoreRefresh, LoadBackgroundPlugins, StopBackgroundPlugins,
    SetOsdEnabled, SetOsdAppearance, Desktop, Plugins, Preferences, Layout, ExitResident, SetDeclaredFnLock, PresentationDemand,
    Tuning, Maintenance, Application
}

public enum PanelFrameKind { Attached = 1, Snapshot, Raise, Result, PluginNavigate }
public enum PanelWakeTarget { Default, Mini, Full, Settings, Updates }
public enum PanelOutcome
{
    Applied = 1, ReadbackConfirmed, CommandSent, Rejected, Busy, Unknown, Detached, TrialStarted,
    ConfigurationSaved, ConfigurationSaveFailed
}

public sealed record PanelViewDraft(string Page, string LightingZone, string TuningReturnPage, bool TuningReturnMini)
{
    [JsonIgnore] public bool Valid =>
        Page is "overview" or "display" or "power" or "devices" or "plugins" or "settings" or "minicustomization" or
            "sleep" or "lightingzone" or "tuning" or "color" or "osdsettings" or "storage" or "diagnostics" or "updates" &&
        LightingZone is "keyboard" or "front" or "hinge" or "logo" &&
        TuningReturnPage is "overview" or "display";
}

public sealed record PanelDraft(bool Compact = false, int? BatteryLimit = null,
    PanelSetKeyboardRgb? Keyboard = null, PanelSetLightingZone[]? Zones = null, PanelSetColor? Color = null,
    int? Brightness = null, int? Refresh = null, int? Monochrome = null, PanelApplyIcc? Icc = null, Guid? PowerPlan = null,
    string[]? OverviewDraft = null, PanelMiniLayout? MiniDraft = null, bool ReplaceMiniLayout = false,
    PanelTuningEditorDraft? Tuning = null, PanelViewDraft? View = null)
{
    [JsonIgnore] public bool Valid => BatteryLimit is (null or 60 or 80 or 100) &&
        (Keyboard == null || Keyboard.Valid) &&
        (Zones == null || Zones.Length <= 3 && Zones.All(zone => zone is { Valid: true }) &&
            Zones.Select(zone => zone.Zone).Distinct(StringComparer.Ordinal).Count() == Zones.Length) &&
        (Color == null || Color is { Valid: true, Factory: false }) &&
        Brightness is (null or >= 10 and <= 100) && Refresh is (null or > 1) &&
        Monochrome is (null or >= 0 and <= 255) && (Icc == null || Icc.Valid) && PowerPlan != Guid.Empty &&
        (OverviewDraft == null || PanelLayoutEdit.ValidOverview(OverviewDraft)) &&
        (MiniDraft == null ? !ReplaceMiniLayout : MiniDraft.Valid) && Tuning is not { Valid: false } &&
        View is not { Valid: false };
}

public sealed record PanelSnapshot(long Revision, bool Ready, bool Active, int? Mode, bool CanSelectMode,
    bool CanSetBatteryLimit, bool CanToggleFanBoost, int? BatteryLimit, bool? FanBoost, bool? RequestedFanBoost,
    bool KeepAwake, bool WaitingForTakeover, PanelGpuSnapshot Gpu, PanelSwitchSnapshot Switches,
    PanelDisplaySnapshot Display, int RunningBackgroundPlugins, PanelOsdSnapshot Osd, long PluginRevision,
    PanelPreferenceSnapshot Preferences, PanelLayouts Layouts, bool LowRefreshOnBattery,
    PanelSystemSummary? SystemSummary = null, PanelDeviceInfo? Device = null, PanelDetailedTelemetry? Details = null,
    PanelThemeSnapshot? Theme = null, long PluginCatalogRevision = 0, PanelUpdateSnapshot? Update = null);

public sealed record PanelGpuSnapshot(bool Fresh, bool Declared, int FirmwareMode, int RuntimeMode, bool RestartPending,
    bool Integrated, bool Hybrid, bool Discrete);

public sealed record PanelSwitchSnapshot(bool? Camera, bool? Microphone, bool? WinLock, bool? Wifi, bool? Bluetooth,
    bool? Touchpad, bool? FnLock, bool CameraAvailable = false, int? MonochromeLevel = null, DateTimeOffset? MonochromeAt = null);

public sealed record PanelDisplaySnapshot(int? Hertz, int[] Rates, int? Brightness,
    bool Active, int Width, int Height, string? Device, string? Panel);
public sealed record PanelRefreshTrial(Guid Id, int Target, DateTimeOffset Deadline);
public sealed record PanelOsdSnapshot(bool Enabled, PanelOsdAppearance Appearance);
public sealed record PanelOsdAppearance(string Theme, int Scale, string Position, string Motion)
{
    [JsonIgnore] public bool Valid => Theme is ("cinema" or "fade" or "pill") &&
        Scale is (80 or 100 or 120 or 140) && Position is ("top-left" or "bottom-center" or "center") &&
        Motion is ("fast" or "normal" or "slow" or "off");
}

public sealed record PanelRequest(int Version, long Id, PanelOperation Operation, int? Value = null, PanelDraft? Draft = null,
    int? Expected = null, Guid? TrialId = null, long? ObservedWake = null, PanelOsdAppearance? OsdAppearance = null,
    PanelDesktopRequest? Desktop = null, PanelPluginRequest? Plugin = null, PanelPreferenceRequest? Preference = null,
    PanelLayoutEdit? Layout = null, PanelPresentationDemand? Demand = null, PanelTuningRequest? Tuning = null,
    PanelMaintenanceRequest? Maintenance = null, PanelApplicationRequest? Application = null)
{
    [JsonIgnore] public bool Valid => ValidArguments && ValidDetails;

    [JsonIgnore] public bool ValidDetails =>
        TrialId != Guid.Empty && Draft is not { Valid: false } &&
        Tuning is not { Valid: false } && Maintenance is not { Valid: false } &&
        Application is not { Valid: false } && Layout is not { Valid: false } &&
        Preference is not { Valid: false } && Plugin is not { Valid: false } &&
        Desktop is not { Valid: false } && OsdAppearance is not { Valid: false };

    private bool ValidArguments => Version == PanelProtocol.Revision && Id > 0 &&
        ((Operation == PanelOperation.PresentationDemand && Demand is { } demand && Enum.IsDefined(demand)) ||
         (Operation != PanelOperation.PresentationDemand && Demand == null)) &&
        (Operation == PanelOperation.Tuning) == (Tuning != null) &&
        (Operation == PanelOperation.Maintenance) == (Maintenance != null) &&
        (Operation == PanelOperation.Application) == (Application != null) &&
        (Operation == PanelOperation.Layout) == (Layout != null) &&
        (Operation == PanelOperation.Preferences) == (Preference != null) &&
        (Operation == PanelOperation.Plugins) == (Plugin != null) &&
        (Operation == PanelOperation.Desktop) == (Desktop != null) &&
        (Operation == PanelOperation.SetOsdAppearance) == (OsdAppearance != null) &&
        ((ObservedWake is >= 0 && Operation == PanelOperation.Detach) ||
         (ObservedWake == null && Operation != PanelOperation.Detach)) &&
        (TrialId.HasValue == (Operation is PanelOperation.KeepRefresh or PanelOperation.RestoreRefresh)) && (Operation switch
    {
        PanelOperation.SelectMode => Value is 1 or 2 or 3 or 4 && Draft == null && Expected == null,
        PanelOperation.SelectGpu => Value is 1 or 2 or 3 && Draft == null && Expected is 1 or 2 or 3,
        PanelOperation.SetBatteryLimit => Value is 60 or 80 or 100 && Draft == null && Expected == null,
        PanelOperation.SetBrightness => Value is >= 10 and <= 100 && Draft == null && Expected == null,
        PanelOperation.SetOsdEnabled or PanelOperation.SetDeclaredFnLock => Value is 0 or 1 && Draft == null && Expected == null,
        PanelOperation.BeginRefresh => Value is > 1 && Expected is > 1 && Value != Expected && Draft == null,
        PanelOperation.KeepRefresh or PanelOperation.RestoreRefresh => Value == null && Draft == null && Expected == null,
        PanelOperation.Detach => Value == null && Expected == null && Draft != null,
        PanelOperation.Takeover or PanelOperation.Release or PanelOperation.ToggleFanBoost or PanelOperation.ToggleKeepAwake or
            PanelOperation.ReadGpu or PanelOperation.ReadSwitches or PanelOperation.ToggleCamera or PanelOperation.ToggleMicrophone or
            PanelOperation.ToggleWinLock or PanelOperation.ToggleWifi or PanelOperation.ToggleBluetooth or
            PanelOperation.ToggleTouchpad or PanelOperation.ToggleFnLock or PanelOperation.ReadDisplay or
            PanelOperation.LoadBackgroundPlugins or PanelOperation.StopBackgroundPlugins or PanelOperation.SetOsdAppearance or
            PanelOperation.Desktop or PanelOperation.Plugins or PanelOperation.Preferences or PanelOperation.Layout or
            PanelOperation.ExitResident or PanelOperation.PresentationDemand or PanelOperation.Tuning or PanelOperation.Maintenance or PanelOperation.Application =>
            Value == null && Draft == null && Expected == null,
        _ => false
    });
}

public sealed record PanelFrame(int Version, PanelFrameKind Kind, long RequestId = 0,
    PanelSnapshot? Snapshot = null, PanelOutcome? Outcome = null, string? Message = null, PanelDraft? Draft = null,
    PanelRefreshTrial? Trial = null, long? Wake = null, PanelDesktopSnapshot? Desktop = null, PanelPluginSnapshot? Plugins = null,
    PanelWakeTarget WakeTarget = PanelWakeTarget.Default, PanelTuningSnapshot? Tuning = null,
    PanelPluginDestination? Destination = null, PanelMaintenanceReply? Maintenance = null, PanelApplicationReply? Application = null)
{
    [JsonIgnore] public bool Valid => Version == PanelProtocol.Revision &&
        (Desktop == null && Plugins == null || Kind == PanelFrameKind.Result && (Desktop == null || Plugins == null)) &&
        (Tuning == null || Kind == PanelFrameKind.Result && Desktop == null && Plugins == null) &&
        (Maintenance == null || Kind == PanelFrameKind.Result && Desktop == null && Plugins == null && Tuning == null) &&
        (Application == null || Kind == PanelFrameKind.Result && Desktop == null && Plugins == null && Tuning == null && Maintenance == null) &&
        (Kind == PanelFrameKind.PluginNavigate ? Destination is { } destination && Enum.IsDefined(destination) : Destination == null) &&
        Enum.IsDefined(WakeTarget) && (WakeTarget == PanelWakeTarget.Default || Kind is PanelFrameKind.Attached or PanelFrameKind.Raise) &&
        (Snapshot == null || Snapshot is { Osd.Appearance.Valid: true, Preferences.Valid: true, Layouts.Mini.Valid: true }
            && Snapshot.Layouts.Overview is { } overview && PanelLayoutEdit.ValidOverview(overview)) &&
        ((Outcome == PanelOutcome.TrialStarted) == (Trial != null)) && (Kind switch
    {
        PanelFrameKind.Attached => RequestId == 0 && Snapshot != null && Draft is { Valid: true } && Outcome == null && Wake is >= 0,
        PanelFrameKind.Snapshot => RequestId == 0 && Snapshot != null && Draft == null && Outcome == null && Wake == null,
        PanelFrameKind.Raise => RequestId == 0 && Snapshot == null && Draft == null && Outcome == null && Wake is > 0,
        PanelFrameKind.PluginNavigate => RequestId == 0 && Snapshot == null && Draft == null && Outcome == null && Wake == null,
        PanelFrameKind.Result => RequestId > 0 && Outcome.HasValue && Enum.IsDefined(Outcome.Value) && Draft == null && Wake == null,
        _ => false
    });
}
