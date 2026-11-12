using Microsoft.Win32;

namespace OpenRevo.Core;

public sealed record DeviceIdentity(string Manufacturer, string Product, string Board, string Processor, string[] Gpus)
{
    public static string ReadBiosVersion(){using var bios=Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");return bios?.GetValue("BIOSVersion")?.ToString()??"未知";}
    public static DeviceIdentity Read()
    {
        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        using var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        var gpus = new List<string>();
        using var display = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (display != null) foreach (var name in display.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
        {
            using var item = display.OpenSubKey(name);
            if (item?.GetValue("DriverDesc") is string desc) gpus.Add(desc);
        }
        return new(bios?.GetValue("SystemManufacturer")?.ToString() ?? "未知",
            bios?.GetValue("SystemProductName")?.ToString() ?? "未知",
            bios?.GetValue("BaseBoardProduct")?.ToString() ?? "未知",
            cpu?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "未知", gpus.Distinct().ToArray());
    }
}
