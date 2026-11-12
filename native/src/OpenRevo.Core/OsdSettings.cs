namespace OpenRevo.Core;

public sealed record OsdSettingsSnapshot(bool Enabled, SystemPreferences Appearance);

public sealed class OsdSettings
{
    private readonly object gate = new();
    private OsdSettingsSnapshot snapshot = new(AppData.Load().OsdEnabled, SystemPreferences.Load());

    public OsdSettingsSnapshot Snapshot => Volatile.Read(ref snapshot);

    public void SetEnabled(bool enabled)
    {
        lock (gate)
        {
            if (snapshot.Enabled == enabled) return;
            var next = AppData.Edit(new { Kind = "OsdEnabled", Value = enabled }, value => value with { OsdEnabled = enabled });
            Volatile.Write(ref snapshot, snapshot with { Enabled = next.OsdEnabled });
        }
    }

    public void SetAppearance(string theme, int scale, string position, string motion)
    {
        lock (gate)
        {
            var updated = SystemPreferences.Edit(
                new { Kind = "OsdAppearance", Theme = theme, Scale = scale, Position = position, Motion = motion }, value => value with
            {
                OsdTheme = theme, OsdScale = scale, OsdPosition = position, MotionSpeed = motion
            });
            Volatile.Write(ref snapshot, snapshot with { Appearance = updated });
        }
    }
}
