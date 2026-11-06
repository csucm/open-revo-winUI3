using System.Text.Json;
namespace OpenRevo.Core;

// Display preferences only. No service or device state is stored here.
public sealed record MiniLayout(int Version, string[] Top, string?[] Quick)
{
    public const int CurrentVersion = 1;
    public static readonly string[] TopIds = ["performance", "gpu", "refresh", "brightness", "keyboard", "battery"];
    public static readonly string[] QuickIds = ["touchpad", "win", "fn", "camera", "mic", "osd", "boost", "awake", "screen"];
    public static MiniLayout Default => new(CurrentVersion, [.. TopIds], ["boost", "camera", "mic", "win", "awake", "screen"]);
    public MiniLayout Copy() => new(Version, [.. Top], [.. Quick]);
    public bool SameAs(MiniLayout other) => Version == other.Version && Top.SequenceEqual(other.Top) && Quick.SequenceEqual(other.Quick);
    private static string FilePath(string? directory) => Path.Combine(directory ?? AppData.DirectoryPath, "mini-layout.json");
    private static void Validate(MiniLayout layout)
    {
        if (layout.Version != CurrentVersion) throw new InvalidDataException("小面板布局版本不受支持，原文件已保留。");
        if (layout.Top == null || layout.Quick == null || layout.Top.Length > TopIds.Length || layout.Quick.Length != 6 ||
            layout.Top.Any(id => !TopIds.Contains(id)) || layout.Quick.Any(id => id != null && !QuickIds.Contains(id)) ||
            layout.Top.Distinct().Count() != layout.Top.Length || layout.Quick.OfType<string>().Distinct().Count() != layout.Quick.Count(id => id != null))
            throw new InvalidDataException("小面板布局含未知或重复控件，原文件已保留。");
    }
    private static MiniLayout Read(string path)
    {
        if (new FileInfo(path).Length > 8192) throw new InvalidDataException("小面板布局文件过大，原文件已保留。");
        var layout = JsonSerializer.Deserialize<MiniLayout>(File.ReadAllText(path)) ?? throw new InvalidDataException("小面板布局为空。");
        Validate(layout);
        return layout;
    }
    public static MiniLayoutRead Load(string? directory = null)
    {
        try { string path = FilePath(directory); return new(File.Exists(path) ? Read(path) : Default, null); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return new(Default, error.Message); }
    }
    public static void Save(MiniLayout layout, bool replaceUnrecognized = false, string? directory = null)
    {
        Validate(layout);
        string path = FilePath(directory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            try { Read(path); }
            catch (Exception error) when (error is InvalidDataException or JsonException)
            {
                if (!replaceUnrecognized) throw new InvalidDataException("原布局无法识别。请先明确恢复默认，再保存。", error);
                // An explicit reset may replace an unreadable format, but keeps its bytes.
                File.Copy(path, path + ".preserved-" + Guid.NewGuid().ToString("N"), false);
            }
        }
        AtomicPreferenceFile.Write(path, JsonSerializer.Serialize(layout));
    }
}
public sealed record MiniLayoutRead(MiniLayout Layout, string? Issue);
