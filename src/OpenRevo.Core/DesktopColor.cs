using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenRevo.Core;

public enum ColorParameter { TemperatureKelvin, Saturation, Contrast, Gamma, RedGain, GreenGain, BlueGain }

public sealed class ColorPreset
{
    private readonly JsonElement source;
    private JsonObject? draft;
    private ColorPreset(JsonElement source) => this.source = source;
    private ColorPreset(JsonObject draft) => this.draft = draft;
    // Read snapshots share their immutable JSON storage. Allocate editable nodes only on the first change.
    private JsonObject Draft => draft ??= JsonNode.Parse(source.GetRawText())!.AsObject();
    public string? Id => Text("id");
    public string? Name => Text("name");
    public static ColorPreset CreateDraft(string id, string name) => new(new JsonObject { ["id"] = id, ["name"] = name });
    private string? Text(string key) => draft != null ? draft[key]?.GetValue<string>() :
        source.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
    public static ColorPreset Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return FromElement(document.RootElement.Clone());
    }
    internal static ColorPreset FromElement(JsonElement source) => source.ValueKind == JsonValueKind.Object ? new(source) : throw new IOException("色彩方案为空。");
    public ColorPreset Copy() => draft == null ? new(source) : new((JsonObject)draft.DeepClone());
    public string Export() => JsonSerializer.Serialize(Payload, new JsonSerializerOptions { WriteIndented = true });
    internal object Payload => draft is {} value ? value : source;

    private static string Key(ColorParameter parameter) => parameter switch
    {
        ColorParameter.TemperatureKelvin => "color_temp_k",
        ColorParameter.Saturation => "saturation",
        ColorParameter.Contrast => "contrast",
        ColorParameter.Gamma => "gamma",
        ColorParameter.RedGain => "gain_r",
        ColorParameter.GreenGain => "gain_g",
        ColorParameter.BlueGain => "gain_b",
        _ => throw new ArgumentOutOfRangeException(nameof(parameter))
    };
    public static (double Minimum, double Maximum) Range(ColorParameter parameter) => parameter switch
    {
        ColorParameter.TemperatureKelvin => (4000, 9000),
        ColorParameter.Saturation => (0, 2),
        ColorParameter.Gamma => (.7, 1.3),
        ColorParameter.Contrast or ColorParameter.RedGain or ColorParameter.GreenGain or ColorParameter.BlueGain => (.5, 1.5),
        _ => throw new ArgumentOutOfRangeException(nameof(parameter))
    };
    public double? Get(ColorParameter parameter)
    {
        string key = Key(parameter);
        if (draft == null)
            return source.TryGetProperty(key, out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetDouble(out double result) ? result : null;
        if (draft[key] is not JsonValue value) return null;
        if (value.TryGetValue<double>(out double floating)) return floating;
        if (value.TryGetValue<int>(out int integer)) return integer;
        if (value.TryGetValue<long>(out long wideInteger)) return wideInteger;
        return null;
    }
    public void Set(ColorParameter parameter, double value)
    {
        var range = Range(parameter);
        if (!double.IsFinite(value) || value < range.Minimum || value > range.Maximum)
            throw new IOException("色彩参数超出范围。");
        // Rust deserializes color_temp_k as u32. Keep the established integer payload.
        if (parameter == ColorParameter.TemperatureKelvin) Draft[Key(parameter)] = (int)value;
        else Draft[Key(parameter)] = value;
    }
    public void ImportParameters(string json)
    {
        var imported = Parse(json);
        foreach (var parameter in Enum.GetValues<ColorParameter>())
        {
            double value = imported.Get(parameter) ?? throw new IOException("色彩参数不完整。");
            var range = Range(parameter);
            if (!double.IsFinite(value) || value < range.Minimum || value > range.Maximum)
                throw new IOException("色彩参数超出范围。");
        }
        // Preserve imported numeric representations and all existing preset metadata.
        foreach (var parameter in Enum.GetValues<ColorParameter>())
            Draft[Key(parameter)] = imported.Draft[Key(parameter)]!.DeepClone();
    }
}

public sealed class ColorServiceState
{
    private readonly JsonElement source;
    private ColorServiceState(JsonElement source)
    {
        this.source = source;
        SavedEnabled = source.GetProperty("enabled").GetBoolean();
        RuntimeEnabled = Boolean(source, "runtime_enabled");
        GammaApplied = Boolean(source, "gamma_applied");
        EngineError = source.TryGetProperty("engine_error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null;
        ActivePreset = ColorPreset.FromElement(source.GetProperty("active_preset"));
        Presets = ReadPresets(source.GetProperty("presets"));
        FactoryPresets = source.TryGetProperty("factory_presets", out var factory) ? ReadPresets(factory) : [];
    }
    public bool SavedEnabled { get; }
    public bool? RuntimeEnabled { get; }
    public bool? GammaApplied { get; }
    public string? EngineError { get; }
    public ColorPreset ActivePreset { get; }
    public IReadOnlyList<ColorPreset> Presets { get; }
    public IReadOnlyList<ColorPreset> FactoryPresets { get; }
    public object Diagnostics => source;
    internal static ColorServiceState Parse(JsonElement source) => new(source);
    private static ColorPreset[] ReadPresets(JsonElement array) => array.EnumerateArray().Select(ColorPreset.FromElement).ToArray();
    private static bool? Boolean(JsonElement source, string key) => source.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
}

public sealed record DesktopPowerState(byte? Trigger, byte? Recovery)
{
    internal static DesktopPowerState Parse(JsonElement source) => new(Byte(source, "trigger"), Byte(source, "recovery"));
    private static byte? Byte(JsonElement source, string key) => source.ValueKind == JsonValueKind.Object && source.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetByte(out byte number) ? number : null;
}

public sealed partial class UpstreamCoreLibrary
{
    public DesktopPowerState ReadDesktopPower() => DesktopPowerState.Parse(Read(13));
    public ColorServiceState ReadColorService() => ColorServiceState.Parse(Read(16));
    public void ApplyColor(bool enabled, ColorPreset preset) => ExecuteFeature(7, new { enabled, preset = preset.Payload });
}
