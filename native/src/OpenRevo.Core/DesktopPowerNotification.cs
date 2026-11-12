using System.Runtime.InteropServices;

namespace OpenRevo.Core;

public sealed class DesktopPowerNotification : IDisposable
{
    private static readonly Guid LidPowerGuid = new("5d3e9a59-e9d5-4b00-a6bd-ff34ff516548");
    private nint registration;

    public DesktopPowerNotification(nint window)
    {
        Guid lid = LidPowerGuid;
        registration = RegisterPowerSettingNotification(window, ref lid, 0);
    }

    public bool LidRegistered => registration != 0;

    public static bool? ReadSuspension(uint message, nuint wParam, nint lParam)
    {
        if (message != 0x0218) return null;
        if (wParam == 0x8013 && lParam != 0 && Marshal.PtrToStructure<Guid>(lParam) == LidPowerGuid &&
            Marshal.ReadInt32(lParam, 16) == sizeof(int))
            return Marshal.ReadInt32(lParam, 20) == 0;
        return wParam switch { 4 => true, 7 or 18 => false, _ => null };
    }

    public void Dispose()
    {
        nint handle = Interlocked.Exchange(ref registration, 0);
        if (handle != 0) UnregisterPowerSettingNotification(handle);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint RegisterPowerSettingNotification(nint window, ref Guid setting, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterPowerSettingNotification(nint notification);
}
