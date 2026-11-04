using System.Text.Json;
namespace OpenRevo.Core;
public static class GpuModeCache
{
    private sealed record Entry(string Device,long BootUnix,long CapturedUnix,GpuModeState State);
    private static string DeviceKey()
    {
        var device = DeviceIdentity.Read();
        return JsonSerializer.Serialize(new[] { device.Manufacturer, device.Product, device.Board, DeviceIdentity.ReadBiosVersion() });
    }
    private static string FilePath=>Path.Combine(AppData.DirectoryPath,"gpu-read-cache.json");
    private static long BootUnix=>DateTimeOffset.UtcNow.ToUnixTimeSeconds()-Environment.TickCount64/1000;
    public static GpuModeState? Read()
    {
        try
        {
            var info=new FileInfo(FilePath);if(!info.Exists||info.Length>4096)return null;
            var saved=JsonSerializer.Deserialize<Entry>(File.ReadAllText(FilePath));
            long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return saved!=null&&saved.Device==DeviceKey()&&Math.Abs(saved.BootUnix-BootUnix)<=5&&saved.CapturedUnix<=now+5&&saved.CapturedUnix>=BootUnix&&saved.State.Known?saved.State:null;
        }
        catch{return null;}
    }
    public static void Save(GpuModeState state)
    {
        if(!state.Known)return;
        // Display cache only. The privileged helper independently reads real
        // firmware and validates capabilities and expected mode before every save.
        Directory.CreateDirectory(AppData.DirectoryPath);
        File.WriteAllText(FilePath,JsonSerializer.Serialize(new Entry(DeviceKey(),BootUnix,DateTimeOffset.UtcNow.ToUnixTimeSeconds(),state)));
    }
}

