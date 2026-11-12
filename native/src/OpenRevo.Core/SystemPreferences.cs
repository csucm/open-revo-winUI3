using System.Text.Json;
namespace OpenRevo.Core;
public sealed record SystemPreferences(string WakeMode="remember",bool MiniAutoHide=true,bool LastMini=false,bool UnlockBatteryLimit=false,
    string OsdTheme="cinema",int OsdScale=100,string OsdPosition="top-left",int LogLevel=1,string LogFilter="",string MotionSpeed="normal",
    bool TrayTelemetry=false)
{
    private static readonly object saveGate = new();
    private static string? nativeDirectory;

    public static void UseNativeStore()
    {
        string directory = UserDataStore.ValidateDirectory(AppData.DirectoryPath);
        UserDataStore.ValidateManaged(directory);
        Volatile.Write(ref nativeDirectory, directory);
    }

    public static SystemPreferences Update(Func<SystemPreferences, SystemPreferences> update)
    {
        lock (saveGate)
        {
            var next = update(Load());
            next.Save();
            return next;
        }
    }

    internal static SystemPreferences Edit<T>(T edit, Func<SystemPreferences, SystemPreferences> legacy)
    {
        if (Volatile.Read(ref nativeDirectory) is not { } directory) return Update(legacy);
        lock (saveGate) return RustSystemPreferences.Edit(directory, edit);
    }

    public static void SetResidentLogging(int level, string filter) =>
        Edit(new { Kind = "Logging", Level = level, Filter = filter },
            value => value with { LogLevel = level, LogFilter = filter });

    public static SystemPreferences Load(string? folder=null)
    {
        if (folder == null && Volatile.Read(ref nativeDirectory) is { } directory)
            return RustSystemPreferences.Load(directory);
        try{string path=Path.Combine(folder??AppData.DirectoryPath,"system-settings.json");if(!File.Exists(path)||new FileInfo(path).Length>8192)return new();var p=JsonSerializer.Deserialize<SystemPreferences>(File.ReadAllText(path));return p!=null&&p.Valid?p:new();}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException){return new();}
    }
    private bool Valid=>WakeMode is "remember" or "mini" or "full"&&OsdTheme is "cinema" or "fade" or "pill"&&OsdScale is 80 or 100 or 120 or 140&&OsdPosition is "top-left" or "bottom-center" or "center"&&LogLevel is >=0 and <=2&&LogFilter is {Length:<=100}&&MotionSpeed is ("fast" or "normal" or "slow" or "off");
    public void Save(string? folder=null)
    {
        if (folder == null && Volatile.Read(ref nativeDirectory) is { } directory)
        {
            lock (saveGate) RustSystemPreferences.Save(directory, this);
            return;
        }
        if(!Valid)throw new ArgumentException("系统设置无效。");string dir=folder??AppData.DirectoryPath;Directory.CreateDirectory(dir);string file=Path.Combine(dir,"system-settings.json");
        lock(saveGate)AtomicPreferenceFile.Write(file,JsonSerializer.Serialize(this));
    }
}
