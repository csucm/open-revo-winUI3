using System.Text.Json;
namespace OpenRevo.Core;
public sealed record SystemPreferences(string WakeMode="remember",bool MiniAutoHide=true,bool LastMini=true,bool UnlockBatteryLimit=false,
    string OsdTheme="cinema",int OsdScale=100,string OsdPosition="top-left",int LogLevel=1,string LogFilter="")
{
    private static readonly object saveGate = new();
    public static SystemPreferences Load(string? folder=null)
    {
        try{string path=Path.Combine(folder??AppData.DirectoryPath,"system-settings.json");if(!File.Exists(path)||new FileInfo(path).Length>8192)return new();var p=JsonSerializer.Deserialize<SystemPreferences>(File.ReadAllText(path));return p!=null&&p.Valid?p:new();}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException){return new();}
    }
    private bool Valid=>WakeMode is "remember" or "mini" or "full"&&OsdTheme is "cinema" or "fade" or "pill"&&OsdScale is 80 or 100 or 120 or 140&&OsdPosition is "top-left" or "bottom-center" or "center"&&LogLevel is >=0 and <=2&&LogFilter is {Length:<=100};
    public void Save(string? folder=null)
    {
        if(!Valid)throw new ArgumentException("系统设置无效。");string dir=folder??AppData.DirectoryPath;Directory.CreateDirectory(dir);string file=Path.Combine(dir,"system-settings.json");
        lock(saveGate)AtomicPreferenceFile.Write(file,JsonSerializer.Serialize(this));
    }
}
