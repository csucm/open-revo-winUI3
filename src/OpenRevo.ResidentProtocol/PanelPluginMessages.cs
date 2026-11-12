using System.Text.Json.Serialization;

namespace OpenRevo.ResidentProtocol;

public enum PanelPluginOperation { Refresh = 1, Enable, Disable, Revoke, ReadView, Input, CloseViews, OpenViews, ReadState, OpenDirectory }

public enum PanelPluginDestination { Overview = 1, Display, Power, Devices, Settings }

public sealed record PanelPluginKey(string Id, string Digest)
{
    internal static bool IsIdentifier(string? value) => value is { Length: > 0 and <= 64 } &&
        char.IsAsciiLetterOrDigit(value[0]) && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    [JsonIgnore] public bool Valid => IsIdentifier(Id) && Digest is { Length: 64 } && Digest.All(char.IsAsciiHexDigit);
}

public sealed record PanelPluginRequest(PanelPluginOperation Operation, PanelPluginKey? Target = null,
    string[]? Grants = null, string? ControlId = null, string? Value = null)
{
    [JsonIgnore] public bool Valid =>
        (Operation == PanelPluginOperation.Input
            ? PanelPluginKey.IsIdentifier(ControlId) && (Value == null || PanelPluginKey.IsIdentifier(Value))
            : ControlId == null && Value == null) &&
        (Grants == null || Operation == PanelPluginOperation.Enable && Grants.Length <= 16 &&
            Grants.All(g => g is { Length: > 0 and <= 64 } && g.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')) &&
            Grants.Distinct(StringComparer.Ordinal).Count() == Grants.Length) &&
        (Operation switch
        {
            PanelPluginOperation.Refresh or PanelPluginOperation.CloseViews or PanelPluginOperation.OpenViews or
                PanelPluginOperation.ReadState or PanelPluginOperation.OpenDirectory => Target == null,
            PanelPluginOperation.Enable or PanelPluginOperation.Disable or PanelPluginOperation.Revoke or
            PanelPluginOperation.ReadView or PanelPluginOperation.Input => Target is { Valid: true },
            _ => false
        });
}

public sealed record PanelPluginSnapshot(PanelPluginEntry[]? Entries = null, string[]? Errors = null, PanelPluginPage? Page = null,
    string? Directory = null);
public sealed record PanelPluginEntry(PanelPluginKey Key, string Name, string Version, string? Description, string? Author,
    string[] Requested, string[]? Granted, bool Background, bool Supported, bool Enabled, bool Running, string? Fault,
    string Folder = "", bool Theme = false, bool HasView = false)
{
    public override string ToString() => Name + (Running ? " · 运行中" : Enabled ? " · 已启用" : "");
}
public sealed record PanelPluginPage(PanelPluginKey Key, bool Running, string? Fault, string? Title, PanelPluginControl[] Controls);
public sealed record PanelPluginControl(string Id, string Kind, string Label, string? Value, bool Enabled, PanelPluginChoice[]? Choices);
public sealed record PanelPluginChoice(string Value, string Label) { public override string ToString() => Label; }
public sealed record PanelThemeColor(string Surface, string Mode, string Token, string Value);
public sealed record PanelThemeSnapshot(long Revision, PanelThemeColor[] Colors);
