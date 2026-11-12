namespace OpenRevo.Core;

public sealed record PanelSettingsState(int Theme, int SampleSeconds, string WakeMode, bool MiniAutoHide,
    bool LastMini, bool UnlockBatteryLimit, bool TrayTelemetry = false);

// Owns the panel-related settings in the resident composition. Other domains
// update only their own keys through the same atomic preference files.
public sealed class PanelSettingsRuntime
{
    private readonly object gate = new();
    private PanelSettingsState state = Load();

    public PanelSettingsState Snapshot => Volatile.Read(ref state);

    private static PanelSettingsState Load()
    {
        var application = AppData.Load();
        var system = SystemPreferences.Load();
        return new(application.Theme, application.SampleSeconds, system.WakeMode,
            system.MiniAutoHide, system.LastMini, system.UnlockBatteryLimit, system.TrayTelemetry);
    }

    public void SetTheme(int theme)
    {
        lock (gate)
        {
            var next = AppData.Edit(new { Kind = "Theme", Value = theme }, value => value with { Theme = theme });
            Volatile.Write(ref state, state with { Theme = next.Theme });
        }
    }

    public void SetSampleSeconds(int seconds)
    {
        lock (gate)
        {
            var next = AppData.Edit(new { Kind = "SampleSeconds", Value = seconds }, value => value with { SampleSeconds = seconds });
            Volatile.Write(ref state, state with { SampleSeconds = next.SampleSeconds });
        }
    }

    public void SetWakeMode(string mode) =>
        UpdateSystem(new { Kind = "WakeMode", Value = mode }, value => value with { WakeMode = mode });
    public void SetMiniAutoHide(bool enabled) =>
        UpdateSystem(new { Kind = "MiniAutoHide", Value = enabled }, value => value with { MiniAutoHide = enabled });
    public void SetTrayTelemetry(bool enabled) =>
        UpdateSystem(new { Kind = "TrayTelemetry", Value = enabled }, value => value with { TrayTelemetry = enabled });
    public void RememberPanelShape(bool mini) =>
        UpdateSystem(new { Kind = "LastMini", Value = mini }, value => value with { LastMini = mini });
    public void SetBatteryOverride(bool enabled) =>
        UpdateSystem(new { Kind = "UnlockBatteryLimit", Value = enabled }, value => value with { UnlockBatteryLimit = enabled });

    private void UpdateSystem<T>(T edit, Func<SystemPreferences, SystemPreferences> legacy)
    {
        lock (gate)
        {
            var next = SystemPreferences.Edit(edit, legacy);
            Volatile.Write(ref state, state with
            {
                WakeMode = next.WakeMode, MiniAutoHide = next.MiniAutoHide, LastMini = next.LastMini,
                UnlockBatteryLimit = next.UnlockBatteryLimit, TrayTelemetry = next.TrayTelemetry
            });
        }
    }
}
