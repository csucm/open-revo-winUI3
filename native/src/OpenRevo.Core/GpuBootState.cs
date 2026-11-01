using System.Text.Json;

namespace OpenRevo.Core;

// Firmware reads describe the next boot, not the display currently running.
// Preserve the first mode across app restarts in this Windows boot.
public sealed record GpuBootState(long BootUnix,int InitialMode,int FirmwareMode,bool LegacyPending=false)
{
    public bool Pending=>InitialMode is >=1 and <=3 ? FirmwareMode!=InitialMode : LegacyPending;
    public bool IsCurrentBoot(long bootUnix)=>Math.Abs(BootUnix-bootUnix)<=5;
    public GpuBootState Observe(int mode)
    {
        if(mode is <1 or >3)throw new ArgumentOutOfRangeException(nameof(mode));
        return this with{FirmwareMode=mode};
    }
}

public static class GpuBootStateStore
{
    public static long BootUnix=>DateTimeOffset.UtcNow.ToUnixTimeSeconds()-Environment.TickCount64/1000;
    private static string FilePath=>Path.Combine(AppData.DirectoryPath,"gpu-boot-state.json");
    public static GpuBootState Open(int firmwareMode,int legacyRequested=0,long legacySavedAt=0)
    {
        long boot=BootUnix;
        try
        {
            var info=new FileInfo(FilePath);
            if(info.Exists&&info.Length<=4096)
            {
                var value=JsonSerializer.Deserialize<GpuBootState>(File.ReadAllText(FilePath));
                if(value!=null&&value.InitialMode is >=0 and <=3&&value.IsCurrentBoot(boot))return value.Observe(firmwareMode);
            }
        }
        catch(IOException){}catch(JsonException){}
        bool legacyPending=legacyRequested is >=1 and <=3&&legacySavedAt>=boot-5;
        int initial=legacyPending?RecoverInitialMode(firmwareMode,boot):firmwareMode;
        return new GpuBootState(boot,initial,firmwareMode,legacyPending).Observe(firmwareMode);
    }
    private static int RecoverInitialMode(int current,long boot)
    {
        try
        {
            string path=Path.Combine(AppData.DirectoryPath,"hardware-audit.jsonl");
            if(!File.Exists(path)||new FileInfo(path).Length>4*1024*1024)return 0;
            return RecoverAudit(File.ReadLines(path),current,boot);
        }
        catch{return 0;}
    }
    // One-time migration of this app's old pending flag. Require a continuous
    // intent/confirmed chain ending at the independently read firmware mode.
    public static int RecoverAudit(IEnumerable<string> lines,int current,long boot)
    {
        int initial=0,last=0,pending=0;
        try
        {
            foreach(string line in lines)
            {
                using var json=JsonDocument.Parse(line);var item=json.RootElement;
                if(!item.TryGetProperty("operation",out var op)||op.GetString() is not ("gpu-next-boot-intent" or "gpu-next-boot-confirmed"))continue;
                if(!item.TryGetProperty("at",out var at)||at.GetDateTimeOffset().ToUnixTimeSeconds()<boot-5)continue;
                if(op.GetString()=="gpu-next-boot-intent")
                {
                    int before=item.GetProperty("before").GetProperty("Mode").GetInt32(),target=item.GetProperty("target").GetInt32();
                    if(pending!=0||before is <1 or >3||target is <1 or >3||before==target||last!=0&&before!=last)return 0;
                    if(initial==0)initial=before;
                    last=before;pending=target;
                }
                else
                {
                    var receipt=item.GetProperty("receipt");
                    if(pending==0||receipt.GetProperty("Before").GetProperty("Mode").GetInt32()!=last||receipt.GetProperty("After").GetProperty("Mode").GetInt32()!=pending||!receipt.GetProperty("Changed").GetBoolean())return 0;
                    last=pending;pending=0;
                }
            }
            return pending==0&&last==current?initial:0;
        }
        catch{return 0;}
    }
    public static void Save(GpuBootState value)
    {
        Directory.CreateDirectory(AppData.DirectoryPath);
        string temp=FilePath+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(temp,JsonSerializer.Serialize(value));File.Move(temp,FilePath,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
