using System.Diagnostics;
using System.Text.Json;
namespace OpenRevo.Core;
public static class PowerPlanRecovery
{
    private static readonly Guid Balanced=new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly HashSet<Guid> Builtins=[Balanced,new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"),new("a1841308-3541-4fab-bc81-f71556f20b4a"),new("e9a42b02-d5df-448d-aa00-03f14749eb61")];
    private static void Run(params string[] arguments)
    {
        var info=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"powercfg.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string arg in arguments)info.ArgumentList.Add(arg);
        using var process=Process.Start(info)??throw new IOException("无法启动电源配置工具。");
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(30000))throw new IOException("电源配置尚未完成，停止后续操作。");
        Task.WhenAll(stdout,stderr).GetAwaiter().GetResult();if(process.ExitCode!=0)throw new IOException("电源配置失败："+stderr.Result);
    }
    public static string Restore(UpstreamCoreLibrary core)
    {
        var plans=new WindowsPowerPlans();var before=plans.List();Guid active=plans.Active();
        string backup=Path.Combine(AppData.DirectoryPath,"power-plan-backups",DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..8]);Directory.CreateDirectory(backup);
        foreach(var plan in before){string file=Path.Combine(backup,plan.Id.ToString("D")+".pow");Run("/export",file,plan.Id.ToString("D"));if(!File.Exists(file)||new FileInfo(file).Length==0)throw new IOException("电源方案备份不完整，未开始修复。");}
        File.WriteAllText(Path.Combine(backup,"manifest.json"),JsonSerializer.Serialize(new{active,plans=before},new JsonSerializerOptions{WriteIndented=true}));
        Exception? failure=null;
        try{core.ExecuteFeature(8,new{confirmed=true});}catch(Exception e){failure=e;}
        // Preserve user-created schemes even when Core fails partway through recovery.
        foreach(var plan in before.Where(p=>!Builtins.Contains(p.Id)))
            if(!plans.List().Any(p=>p.Id==plan.Id))Run("/import",Path.Combine(backup,plan.Id.ToString("D")+".pow"),plan.Id.ToString("D"));
        if(failure!=null)throw new IOException("修复未完成；自建方案已检查保留，备份："+backup,failure);
        if(plans.Active()!=Balanced)throw new IOException("平衡方案回读未确认；备份："+backup);
        return "Windows平衡方案已恢复并回读；Core已执行OEM调度恢复流程，OEM包效果需实际确认。原方案备份："+backup;
    }
}
