namespace OpenRevo.Core.Hal;

public sealed record KeyboardLighting(byte Red, byte Green, byte Blue, byte Backlight, byte Trigger);
public sealed record LightingReceipt(KeyboardLighting Before, KeyboardLighting After);
public sealed partial class HardwareSession
{
    private LightingReceipt? lastLighting;
    public object? LightingDiagnostics{get;private set;}
    // This firmware clears the backlight bit 4 and trigger bit 5 after consuming
    // the command. They are not persistent state and must not cause false conflicts.
    private static bool SameLighting(KeyboardLighting a, KeyboardLighting b) => a.Red == b.Red && a.Green == b.Green && a.Blue == b.Blue
        && (a.Backlight & ~Vault.BacklightCommandMask) == (b.Backlight & ~Vault.BacklightCommandMask)
        && (a.Trigger & ~Vault.RgbEnableMask) == (b.Trigger & ~Vault.RgbEnableMask);
    private static KeyboardLighting ReadLighting(IHardwareTransport ec)
    {
        byte Get(ushort a,string field) => ec.Read(a) ?? throw new IOException("键盘灯光字段 "+field+" 读取失败。");
        return new(Get(Vault.RgbRed,"red"), Get(Vault.RgbGreen,"green"), Get(Vault.RgbBlue,"blue"), Get(Vault.Backlight,"backlight"), Get(Vault.Trigger,"trigger"));
    }
    public KeyboardLighting ProbeLighting() => Serialized(() =>
    {
        using var trace=DiagnosticLog.Begin("lighting.probe");
        try{Guard();using var ec=open();var state=ReadLighting(ec);LightingDiagnostics=new{stage="probe",success=true,state};trace.Complete(state);return state;}
        catch(Exception error){LightingDiagnostics=new{stage="probe",success=false,error=error.Message};trace.Fail(error);throw;}
    });
    private LightingReceipt ChangeLighting(Func<KeyboardLighting, KeyboardLighting> target, KeyboardLighting? required = null)
        => Serialized(() =>
    {
        using var trace=DiagnosticLog.Begin("lighting.apply");
        try
        {
        if (!Active) throw new InvalidOperationException("请先启用硬件接管。");
        Guard(); using var ec = open(); var control = Read(ec);
        DiagnosticLog.Record("lighting.capabilities",new{control.Project,control.Support2,core=useCoreAdmission?CoreMachine.Current:null});
        if (!MatchesProject(control.Project) || (control.Support2 & Vault.SingleZoneSupportMask) == 0)
            throw new InvalidOperationException("本机未匹配已适配的单区 RGB 协议。");
        var isolated = ec.Read(Vault.FanIsolated);
        DiagnosticLog.Record("lighting.preflight",new{isolated,conflictMask=7});
        if (!isolated.HasValue || (isolated.Value & 7) != 0) throw new InvalidOperationException("当前风扇隔离控制状态与单区灯光通道冲突，未执行灯光写入。");
        var before = ReadLighting(ec);
        if (required != null && !SameLighting(before, required)) throw new InvalidOperationException("灯光状态已被外部更改，未执行撤销。");
        var after = target(before);
        DiagnosticLog.Record("lighting.request",new{before,target=after});
        audit(new { at = DateTimeOffset.Now, operation = "keyboard-lighting-intent", before, after });
        try
        {
            void Write(ushort address, byte value)
            {
                Guard();
                if (!ec.Write(address, value)) throw new IOException("灯光写入失败。");
                int ignore = address == Vault.Trigger ? Vault.RgbEnableMask : address == Vault.Backlight ? Vault.BacklightCommandMask : 0;
                byte? read=null;
                for(int attempt=0;attempt<6;attempt++){if(attempt>0)Thread.Sleep(20);read=ec.Read(address);if(read.HasValue&&(read.Value&~ignore)==(value&~ignore))break;}
                DiagnosticLog.Record("lighting.readback",new{field=address==Vault.RgbRed?"red":address==Vault.RgbGreen?"green":address==Vault.RgbBlue?"blue":address==Vault.Backlight?"backlight":"trigger",expected=value,actual=read,ignoredMask=ignore});
                if (!read.HasValue || (read.Value & ~ignore) != (value & ~ignore)) throw new IOException("灯光回读失败。");
            }
            if(UsesProductionClassicCore)
            {
                Guard();
                CoreMachine.Library.ExecuteFeature(13,new{kind="lighting",project=control.Project,
                    old_red=before.Red,old_green=before.Green,old_blue=before.Blue,old_backlight=before.Backlight,old_trigger=before.Trigger,
                    red=after.Red,green=after.Green,blue=after.Blue,backlight=after.Backlight,trigger=after.Trigger});
            }
            else
            {
            Write(Vault.RgbRed, after.Red); Write(Vault.RgbGreen, after.Green); Write(Vault.RgbBlue, after.Blue);
            Write(Vault.Backlight, after.Backlight); Write(Vault.Trigger, after.Trigger);
            }
            var confirmed = ReadLighting(ec);
            if (!SameLighting(confirmed, after)) throw new IOException("灯光最终状态与请求不一致。");
            var result = new LightingReceipt(before, confirmed);
            audit(new { at = DateTimeOffset.Now, operation = "keyboard-lighting-confirmed", result });
            LightingDiagnostics=new{stage="confirmed",success=true,result};trace.Complete(result);
            return result;
        }
        catch (Exception e)
        {
            faulted = true;
            try { audit(new { at = DateTimeOffset.Now, operation = "keyboard-lighting-stopped", error = e.Message }); } catch { }
            throw new IOException("灯光操作已停止，部分写入可能已生效。" + e.Message, e);
        }
        }
        catch(Exception error){LightingDiagnostics=new{stage="failed",success=false,error=error.Message};trace.Fail(error);throw;}
    });
    public LightingReceipt SetKeyboardLighting(byte red, byte green, byte blue, int brightness, bool rainbow)
    {
        if (brightness is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(brightness));
        byte Scale(byte channel) => channel == 0 ? (byte)0 : (byte)Math.Clamp(channel * 50 / 255, 1, 50);
        var result = ChangeLighting(current =>
        {
            byte r = Scale(red), g = Scale(green), b = Scale(blue);
            if (red == 255 && green == 255 && blue == 255) (r, g, b) = (50, 40, 40);
            if (rainbow || brightness == 0) (r, g, b) = (0, 0, 0);
            byte backlight = (byte)((current.Backlight & 13) | (brightness switch { 0 => 18, 1 => 48, 2 => 80, 3 => 112, _ => 144 }));
            byte trigger = (byte)((current.Trigger & ~Vault.RainbowMask) | Vault.RgbEnableMask);
            if (rainbow && brightness > 0) trigger |= Vault.RainbowMask;
            return new(r, g, b, backlight, trigger);
        });
        lastLighting = result; return result;
    }
    public LightingReceipt UndoKeyboardLighting() => Serialized(() =>
    {
        var previous = lastLighting ?? throw new InvalidOperationException("没有本次会话可撤销的灯光修改。");
        var result = ChangeLighting(_ => previous.Before, previous.After); lastLighting = null; return result;
    });
    public LightingReceipt RestoreKeyboardLightingSnapshot(KeyboardLighting saved, KeyboardLighting expectedState)
    {
        if (saved.Red > 50 || saved.Green > 50 || saved.Blue > 50) throw new ArgumentOutOfRangeException(nameof(saved));
        return ChangeLighting(current => saved with { Backlight = (byte)((saved.Backlight & 242) | (current.Backlight & 13)),
            Trigger = (byte)((current.Trigger & ~(Vault.RainbowMask | Vault.RgbEnableMask)) | (saved.Trigger & (Vault.RainbowMask | Vault.RgbEnableMask))) }, expectedState);
    }
}
