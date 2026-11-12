using System.Text.Json;

namespace OpenRevo.Core;

public sealed record AppPreferences(int Theme = 1, int SampleSeconds = 2, bool StartMini = true, bool LowRefreshOnBattery = false, int RequestedGpu = 0, long GpuSavedAtUnix = 0,bool OsdEnabled=true,string? SelectedTuningPreset=null,Hal.DeclaredTuningPreset? DeclaredTuning=null)
{
    public Hal.CpuPowerCeilings? CpuPowerCeilings {get;init;}
}

public static class AppData
{
    private static bool IsResidentCandidate =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "OpenRevo.Resident" &&
        Environment.GetCommandLineArgs() is [_, "--isolated-candidate", ..];
    public static string DefaultDirectoryPath=>IsResidentCandidate
        ? UserDataStore.ValidateDirectory(Path.Combine(AppContext.BaseDirectory,"resident-data"))
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OpenRevoNative");
    public static string LocationPointer=>DefaultDirectoryPath+".location.json";
    internal static string CommittedLocationPointer => LocationPointer + ".committed";
    public static string ResidentStorageRoot =>
        UserDataStore.ValidateDirectory(Path.Combine(AppContext.BaseDirectory, "resident-storage"));
    public static string ValidateResidentStorageDestination(string directory)
    {
        string path = UserDataStore.ValidateDirectory(directory);
        if (!path.StartsWith(ResidentStorageRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择候选后台 resident-storage 下的目录。");
        return path;
    }
    private static readonly Lazy<string> resolvedDirectory=new(ResolveDirectory);
    public static string DirectoryPath=>resolvedDirectory.Value;
    public static bool IsIsolatedReviewData=>(System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name is "OpenRevo.DesignReview" or "OpenRevo.OsdPowerReview"||Environment.GetCommandLineArgs().Any(a=>a is "--ui-settings-checks" or "--ui-theme-restore-checks" or "--ui-design-checks" or "--approved-ui-review" or "--painted-ui-review"))&&Environment.GetEnvironmentVariable("OPENREVO_NATIVE_TEST_DATA_DIR") is string path&&Path.IsPathFullyQualified(path)&&File.Exists(Path.Combine(path,"openrevo-native-data.json"));
    private static string ResolveDirectory()
    {
        if (!IsResidentCandidate && !IsIsolatedReviewData && File.Exists(CommittedLocationPointer))
        {
            var pointer = new FileInfo(CommittedLocationPointer);
            if (pointer.Length >= 8192 || (pointer.Attributes & FileAttributes.ReparsePoint) != 0 ||
                JsonSerializer.Deserialize<string>(File.ReadAllText(pointer.FullName)) is not { } selected)
                throw new IOException("无法验证已提交的 Open-Revo 数据位置。");
            string committed = UserDataStore.ValidateInstalledDataLocation(selected, AppContext.BaseDirectory);
            UserDataStore.ValidateManaged(committed);
            return committed;
        }
        try
        {
            if(IsResidentCandidate)
            {
                if(File.Exists(LocationPointer)&&new FileInfo(LocationPointer) is {Length:<8192,Attributes:var flags}&&
                    (flags&FileAttributes.ReparsePoint)==0&&JsonSerializer.Deserialize<string>(File.ReadAllText(LocationPointer)) is string candidate)
                {
                    var destination=ValidateResidentStorageDestination(candidate);
                    UserDataStore.ValidateManaged(destination);
                    return destination;
                }
                return DefaultDirectoryPath;
            }
            if(IsIsolatedReviewData)return UserDataStore.ValidateDirectory(Environment.GetEnvironmentVariable("OPENREVO_NATIVE_TEST_DATA_DIR")!);
            string portable=Path.Combine(AppContext.BaseDirectory,"data");
            if(File.Exists(Path.Combine(portable,"openrevo-native-data.json")))return UserDataStore.ValidateDirectory(portable);
            if(File.Exists(LocationPointer)&&new FileInfo(LocationPointer).Length<8192){var path=JsonSerializer.Deserialize<string>(File.ReadAllText(LocationPointer));if(path!=null&&File.Exists(Path.Combine(path,"openrevo-native-data.json")))return UserDataStore.ValidateDirectory(path);}
        }
        catch(Exception e)when(e is IOException or JsonException or ArgumentException or UnauthorizedAccessException){}
        return DefaultDirectoryPath;
    }
    private static readonly object preferenceGate=new();
    private static string? nativeDirectory;

    public static void UseNativeStore()
    {
        string directory = UserDataStore.ValidateDirectory(DirectoryPath);
        UserDataStore.ValidateManaged(directory);
        Volatile.Write(ref nativeDirectory, directory);
    }

    public static void Update(Func<AppPreferences,AppPreferences> update,string? folder=null)
    {
        lock (preferenceGate)
        {
            var current = folder == null && Volatile.Read(ref nativeDirectory) is { } directory
                ? RustAppPreferences.Load(directory, forUpdate: true) : Load(folder);
            Save(update(current), folder);
        }
    }

    internal static AppPreferences Edit<T>(T edit, Func<AppPreferences, AppPreferences> legacy)
    {
        lock (preferenceGate)
        {
            if (Volatile.Read(ref nativeDirectory) is { } directory)
                return RustAppPreferences.Edit(directory, edit);
            var next = legacy(Load());
            Save(next);
            return next;
        }
    }
    public static string DiagnosticsPath => Path.Combine(DirectoryPath, "diagnostics");
    public static AppPreferences Load(string? folder = null)
    {
        if (folder == null && Volatile.Read(ref nativeDirectory) is { } directory)
            return RustAppPreferences.Load(directory);
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
        if (folder == null && Volatile.Read(ref nativeDirectory) is { } native)
        {
            lock (preferenceGate) RustAppPreferences.Save(native, value);
            return;
        }
        string directory = folder ?? DirectoryPath;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        lock (preferenceGate) AtomicPreferenceFile.Write(path, JsonSerializer.Serialize(value));
    }
}
