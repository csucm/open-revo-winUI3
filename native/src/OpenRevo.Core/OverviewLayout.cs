using System.Text.Json;
namespace OpenRevo.Core;
public static class OverviewLayout
{
    public static readonly string[] BuiltIns=["cpu","gpu","vram","fan","performance","battery","display"];
    public static string[] Normalize(IEnumerable<string> items)=>items.Where(BuiltIns.Contains).Distinct().ToArray();
    public static string[] Load(string? directory=null)
    {
        string path=Path.Combine(directory??AppData.DirectoryPath,"overview-layout.json");
        try{return File.Exists(path)&&new FileInfo(path).Length<=8192?Normalize(JsonSerializer.Deserialize<string[]>(File.ReadAllText(path))??BuiltIns):[..BuiltIns];}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException){return [..BuiltIns];}
    }
    public static void Save(IEnumerable<string> ids,string? directory=null)
    {
        string folder=directory??AppData.DirectoryPath;Directory.CreateDirectory(folder);
        AtomicPreferenceFile.Write(Path.Combine(folder,"overview-layout.json"),JsonSerializer.Serialize(Normalize(ids)));
    }
}
