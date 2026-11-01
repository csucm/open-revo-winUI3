namespace OpenRevo.Core;

public sealed record KeyboardCapability(string Kind, string Description, bool HasColor, bool HasZones)
{
    public static KeyboardCapability Detect(bool perKey, bool fourZone, bool singleZone, string? modelHint = null)
        => perKey ? new("PerKey", "逐键 RGB", true, false)
         : fourZone ? new("4Zone", "四区 RGB", true, true)
         : singleZone ? new("SingleZone", "单区 RGB", true, false)
         : modelHint is "MonochromeWhite" or "White" ? new("White", "白色背光", false, false)
         : modelHint == "None" ? new("None", "无键盘背光", false, false)
         : new("Unknown", "背光类型未识别", false, false);
}
