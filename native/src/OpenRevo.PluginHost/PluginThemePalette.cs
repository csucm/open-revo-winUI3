using OpenRevo.PluginSdk;

namespace OpenRevo.PluginHost;

// Validated color data only. No executable plugin object enters the UI process.
public sealed class PluginThemePalette
{
    private readonly Dictionary<(string Surface, string Mode, string Token), string> colors = new();
    public bool IsEmpty => colors.Count == 0;
    public static PluginThemePalette Empty { get; } = new();
    public static readonly string[] SupportedSurfaces = ["main", "mini", "menu", "dialog"];
    private PluginThemePalette() { }

    public static string Default(string token, bool dark) => (token, dark) switch
    {
        ("background", false) => "ECEEF2", ("background", true) => "171B24",
        ("surface", false) => "F8F9FB", ("surface", true) => "252B37",
        ("border", false) => "DCDFE4", ("border", true) => "3D4451",
        ("text", false) => "242B38", ("text", true) => "EEF1F8",
        ("muted", false) => "5F697A", ("muted", true) => "ACB6C8",
        ("tint", false) => "DFE6F5", ("tint", true) => "333D51",
        ("subtle", false) => "E8ECF2", ("subtle", true) => "343C4B",
        ("accent", false) => "3468CE", ("accent", true) => "92B9FF",
        _ => throw new ArgumentException("Unknown color token.")
    };
    public string Resolve(string surface, bool dark, string token)
        => colors.GetValueOrDefault((surface, dark ? "dark" : "light", token)) ?? Default(token, dark);
    public bool HasSurface(string surface, bool dark) => colors.Keys.Any(k => k.Surface == surface && k.Mode == (dark ? "dark" : "light"));

    public PluginThemeValue[] ReadColors() => colors.Select(item =>
        new PluginThemeValue(item.Key.Surface, item.Key.Mode, item.Key.Token, item.Value)).ToArray();

    public static PluginThemePalette FromColors(IEnumerable<PluginThemeValue> values)
    {
        var palette = new PluginThemePalette();
        foreach (var color in values)
        {
            if (!SupportedSurfaces.Contains(color.Surface) || color.Mode is not ("light" or "dark") ||
                !PlatformValidation.ColorTokens.Contains(color.Token) ||
                color.Value is not { Length: 6 } || !color.Value.All(char.IsAsciiHexDigit) ||
                !palette.colors.TryAdd((color.Surface, color.Mode, color.Token), color.Value.ToUpperInvariant()))
                throw new InvalidDataException("Invalid theme color.");
        }
        palette.ValidateContrast();
        return palette;
    }

    public static void ValidatePackage(PluginPackage package)
    {
        var manifest = package.Manifest;
        var platform = manifest.Platform;
        if (platform is not { Runtime: "static" } || manifest.Permissions.Length != 1 ||
            manifest.Permissions[0] != PluginPlatformPermissions.Theme ||
            platform.RequiredCapabilities.Any(c => c != PluginPlatformPermissions.Theme) ||
            platform.Contributions.Theme.Length == 0 || platform.Contributions.Pages.Length != 0 ||
            platform.Contributions.Motions.Length != 0 || platform.Contributions.Keys.Length != 0 ||
            platform.Contributions.Policies.Length != 0 ||
            platform.Contributions.Theme.Any(c => !SupportedSurfaces.Contains(c.Surface)))
            throw new InvalidDataException("当前主题入口只支持大面板、mini、菜单和弹窗的静态颜色声明。");
        PlatformValidation.Validate(platform, manifest.Permissions);
    }

    public static PluginThemePalette Compose(IEnumerable<PluginPackage> packages)
    {
        var palette = new PluginThemePalette();
        var owners = new Dictionary<(string, string, string), string>();
        foreach (var package in packages)
        {
            ValidatePackage(package);
            var manifest = package.Manifest;
            foreach (var color in manifest.Platform!.Contributions.Theme)
            {
                var key = (color.Surface, color.Mode, color.Token);
                if (owners.TryGetValue(key, out string? owner))
                    throw new InvalidDataException($"配色与已载入插件“{owner}”冲突。请先停止该插件。");
                owners.Add(key, manifest.Name); palette.colors.Add(key, color.Value.ToUpperInvariant());
            }
        }
        palette.ValidateContrast();
        return palette;
    }

    private void ValidateContrast()
    {
        foreach (string surface in SupportedSurfaces)
        foreach (bool dark in new[] { false, true })
        {
            string Get(string token) => Resolve(surface, dark, token);
            foreach (string fill in new[] { "background", "surface", "tint", "subtle" })
                if (Contrast(Get("text"), Get(fill)) < 4.5)
                    throw new InvalidDataException($"主题的{surface}文字与背景对比度不足。");
            foreach (string fill in new[] { "background", "surface" })
                if (Contrast(Get("muted"), Get(fill)) < 4.5)
                    throw new InvalidDataException($"主题的{surface}说明文字对比度不足。");
            if (Contrast(Get("accent"), dark ? "171B24" : "FFFFFF") < 4.5 ||
                Contrast(Get("accent"), Get("tint")) < 3)
                throw new InvalidDataException($"主题的{surface}按钮或选中状态对比度不足。");
        }
    }
    public static double Contrast(string first, string second)
    {
        static double Luminance(string value)
        {
            double Channel(int at)
            {
                double c = Convert.ToInt32(value.Substring(at, 2), 16) / 255d;
                return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4);
            }
            return .2126 * Channel(0) + .7152 * Channel(2) + .0722 * Channel(4);
        }
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}
