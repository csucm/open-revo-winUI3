using System.Text.Json;
using OpenRevo.Core.Hal;

namespace OpenRevo.Core;

public static class OemFanSnapshotCommand
{
    public static int Run(string selectedMode, string outputPath, bool ownsAppInstance)
    {
        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write);
        var capturedAt = DateTimeOffset.UtcNow;
        DeviceIdentity? device = null;
        string? bios = null;
        OemFanSnapshotReceipt? snapshot = null;
        string? error = null;
        try
        {
            if (selectedMode is not ("office" or "balance" or "turbo" or "custom-on" or "custom-off"))
                throw new ArgumentException("标签须为 office、balance、turbo、custom-on 或 custom-off。");
            if (!ownsAppInstance)
                throw new InvalidOperationException("请先从托盘完全退出 Open-Revo。");
            if (!GpuModeControl.IsAdministrator())
                throw new UnauthorizedAccessException("请以管理员身份打开 PowerShell 后重试。");

            device = DeviceIdentity.Read();
            bios = DeviceIdentity.ReadBiosVersion();
            snapshot = HardwareSession.ForThisMachine().CaptureGm7tg0mFanSnapshot();
        }
        catch (Exception failure)
        {
            error = failure.Message;
        }

        JsonSerializer.Serialize(output, new
        {
            schema = 1,
            result = snapshot is null ? "Unavailable" : "Captured",
            capturedAt,
            selectedOemMode = selectedMode,
            device,
            bios,
            coreBuild = typeof(OemFanSnapshotCommand).Module.ModuleVersionId,
            snapshot,
            error,
            ecWrites = 0
        }, new JsonSerializerOptions { WriteIndented = true });
        return snapshot is null ? 2 : 0;
    }
}
