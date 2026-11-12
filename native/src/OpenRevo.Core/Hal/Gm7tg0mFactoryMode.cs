using Microsoft.Win32;

namespace OpenRevo.Core.Hal;

internal static class Gm7tg0mFactoryMode
{
    internal const byte Project = 16, Platform = 7;
    private static readonly Lazy<bool> matchesMachine = new(() =>
    {
        try
        {
            using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            return MatchesIdentity(bios?.GetValue("BaseBoardProduct") as string, bios?.GetValue("BIOSVersion") as string);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    });

    internal static bool MatchesMachine => matchesMachine.Value;

    internal static bool MatchesIdentity(string? board, string? bios) =>
        string.Equals(board, "GM7TG0M", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(bios, "N.1.07MRO06", StringComparison.OrdinalIgnoreCase);

    internal static int? Decode(int? project, int? platform, int? custom, int? clamp, int? index, int? isolated)
    {
        if (project != Project || platform != Platform || custom != 0 || clamp is not (>= 0 and <= 255) ||
            index != 0x10)
            return null;

        int modeClamp = clamp.Value & ~Vault.FanBoostMask;
        return (modeClamp, isolated) switch
        {
            (0xA0, 0) => 1,
            // The OEM balance table and clamp persisted with either isolation value in r8.
            (0x00, 0 or 0x80) => 2,
            (0x10, 0) => 3,
            _ => null
        };
    }
}

public sealed partial class HardwareSession
{
    private bool CanReadGm7tg0mFactoryMode => useCoreAdmission && Admission.Gm7tg0mModeProfile;
}
