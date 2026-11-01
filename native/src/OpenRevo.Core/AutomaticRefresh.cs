namespace OpenRevo.Core;

/// <summary>Only the validated internal panel is ever passed to this policy.</summary>
public sealed class AutomaticRefresh
{
    private bool? appliedAc;
    private bool wasEnabled;
    private int? restoreRate;
    public bool Pending { get; private set; }
    public void Evaluate(bool enabled, bool? onAc, Func<DisplayState> read, Action<int, int> change)
    {
        if (!onAc.HasValue) return;
        if (!enabled && !wasEnabled && !Pending) return;
        if (enabled && wasEnabled && appliedAc == onAc && !Pending) return;
        var state = read();
        if (!state.Active || state.DeviceName.Length == 0 || state.Rates.Length == 0) { Pending = true; wasEnabled = enabled; return; }
        int target = state.Hertz;
        if (enabled && onAc == false)
        {
            restoreRate ??= state.Hertz > state.Rates.Min() ? state.Hertz : state.Rates.Max();
            target = state.Rates.Min();
        }
        else if (restoreRate is int previous && state.Rates.Contains(previous)) target = previous;
        if (target != state.Hertz) change(state.Hertz, target);
        if (!enabled || onAc == true) restoreRate = null;
        appliedAc = onAc; wasEnabled = enabled; Pending = false;
    }
    public void ManualSelection() { appliedAc = null; restoreRate = null; }
    public void InvalidateTopology() { if (wasEnabled || restoreRate.HasValue) Pending = true; }
}
