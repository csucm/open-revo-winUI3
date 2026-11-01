using System.Text.Json;

namespace OpenRevo.Core;

public sealed record AppPreferences(int Theme = 1, int SampleSeconds = 2, bool StartMini = true, bool LowRefreshOnBattery = false, int RequestedGpu = 0, long GpuSavedAtUnix = 0,bool OsdEnabled=true);

public static class AppData
{
    public static string DefaultDirectoryPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OpenRevoNative");
    public static string LocationPointer=>DefaultDirectoryPath+".location.json";
    private static readonly Lazy<string> resolvedDirectory=new(ResolveDirectory);
    public static string DirectoryPath=>resolvedDirectory.Value;
    public static bool IsIsolatedReviewData=>(System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name is "OpenRevo.DesignReview" or "OpenRevo.OsdPowerReview"||Environment.GetCommandLineArgs().Any(a=>a is "--ui-settings-checks" or "--ui-theme-restore-checks" or "--ui-design-checks"))&&Environment.GetEnvironmentVariable("OPENREVO_NATIVE_TEST_DATA_DIR") is string path&&Path.IsPathFullyQualified(path)&&File.Exists(Path.Combine(path,"openrevo-native-data.json"));
    private static string ResolveDirectory()
    {
        try
        {
            if(IsIsolatedReviewData)return UserDataStore.ValidateDirectory(Environment.GetEnvironmentVariable("OPENREVO_NATIVE_TEST_DATA_DIR")!);
            string portable=Path.Combine(AppContext.BaseDirectory,"data");
            if(File.Exists(Path.Combine(portable,"openrevo-native-data.json")))return UserDataStore.ValidateDirectory(portable);
            if(File.Exists(LocationPointer)&&new FileInfo(LocationPointer).Length<8192){var path=JsonSerializer.Deserialize<string>(File.ReadAllText(LocationPointer));if(path!=null&&File.Exists(Path.Combine(path,"openrevo-native-data.json")))return UserDataStore.ValidateDirectory(path);}
        }
        catch(Exception e)when(e is IOException or JsonException or ArgumentException or UnauthorizedAccessException){}
        return DefaultDirectoryPath;
    }
    private static readonly object preferenceGate=new();
    public static void Update(Func<AppPreferences,AppPreferences> update,string? folder=null)
    {lock(preferenceGate)Save(update(Load(folder)),folder);}
    public static string DiagnosticsPath => Path.Combine(DirectoryPath, "diagnostics");
    public static AppPreferences Load(string? folder = null)
    {
        try
        {
            string path = Path.Combine(folder ?? DirectoryPath, "preferences.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return new();
            var value = JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(path));
            return value is { Theme: >= 0 and <= 2, SampleSeconds: 2 or 5 or 10, RequestedGpu: >= 0 and <= 3, GpuSavedAtUnix: >= 0 } ? value : new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public static void Save(AppPreferences value, string? folder = null)
    {
        if (value.Theme is < 0 or > 2 || value.SampleSeconds is not (2 or 5 or 10) || value.RequestedGpu is < 0 or > 3 || value.GpuSavedAtUnix < 0) throw new ArgumentOutOfRangeException(nameof(value));
        string directory = folder ?? DirectoryPath;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        lock (preferenceGate) AtomicPreferenceFile.Write(path, JsonSerializer.Serialize(value));
    }
}
