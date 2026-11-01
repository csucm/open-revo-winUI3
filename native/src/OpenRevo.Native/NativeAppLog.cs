using OpenRevo.Core;
namespace OpenRevo.Native;
internal static class NativeAppLog
{
    private static readonly object gate=new();
    internal static int Level{get;set;}
    internal static string Filter{get;set;}="";
    internal static string FilePath=>Path.Combine(AppData.DiagnosticsPath,"native.log");
    internal static void Write(string message,bool error=false)
    {
        if(Level==0||Level==1&&!error)return;
        if(!string.IsNullOrEmpty(Filter)&&!message.Contains(Filter,StringComparison.OrdinalIgnoreCase))return;
        try{lock(gate){Directory.CreateDirectory(AppData.DiagnosticsPath);if(File.Exists(FilePath)&&new FileInfo(FilePath).Length>10*1024*1024)File.Move(FilePath,FilePath+".previous",true);File.AppendAllText(FilePath,$"{DateTimeOffset.Now:O} {(error?"ERROR":"INFO")} {message}{Environment.NewLine}");}}catch(IOException){}catch(UnauthorizedAccessException){}
    }
}
internal sealed class NativeActionLog:List<string>
{
    public new void Add(string message){if(Count>=256)RemoveAt(0);base.Add(message);DiagnosticLog.Record("action.recorded",new{message});NativeAppLog.Write(message);}
}
