namespace OpenRevo.Core.Hal;

public sealed partial class HardwareSession
{
    private bool? requestedFanBoost;
    public bool? RequestedFanBoost => Active && CoreDelegated ? requestedFanBoost : null;

    public object ApplyBatteryCommand(int upper)
    {
        if (upper is not (60 or 80 or 100)) throw new ArgumentOutOfRangeException(nameof(upper));
        if (!CoreDelegated) return SetBatteryPreset(upper);
        return SendDeclaredControl(1, (uint)upper, SupportsBattery);
    }

    public object ApplyFanBoostCommand(bool enabled)
    {
        if (!CoreDelegated) return SetFanBoost(enabled);
        return SendDeclaredControl(2, enabled ? 1u : 0u, SupportsFanBoost);
    }

    public bool SupportsDeclaredFnLock => CoreDelegated && Admission.FnLock;

    public object ApplyDeclaredFnLock(bool enabled) =>
        SendDeclaredControl(3, enabled ? 1u : 0u, SupportsDeclaredFnLock);

    public object ApplyDeclaredGpuMux(int mode, IGpuModeCore core) =>
        SendDeclaredControl(4, (uint)mode, CoreDelegated && mode is 2 or 3 && core.ReadGpuMode().Supports(mode));

    public bool SupportsDeclaredRgb => CoreDelegated && Admission.DeclaredRgb;

    public object ApplyDeclaredRgb(uint color, int effect)
    {
        if(color>0xFFFFFF || effect is <0 or >2)throw new ArgumentOutOfRangeException();
        return SendDeclaredControl(5, ((uint)effect<<24)|color, SupportsDeclaredRgb);
    }

    private object SendDeclaredControl(uint feature, uint target, bool supported) => Serialized(() =>
    {
        Guard();
        if (!Active || !supported) throw new NotSupportedException("当前 Core 驱动未提供此控制。");
        audit(new { operation = "core-declared-control-intent", feature, target });
        try
        {
            CoreMachine.Library.SendDeclaredControl(feature, target);
            if (feature == 2) requestedFanBoost = target == 1;
            var receipt = new { operation = "core-declared-control-sent", feature, target, hardwareReadback = false };
            audit(receipt);
            return receipt;
        }
        catch
        {
            requestedFanBoost = null;
            faulted = true;
            throw;
        }
    });
}



