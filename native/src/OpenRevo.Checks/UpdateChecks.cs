using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using OpenRevo.Core;

internal static class UpdateChecks
{
    public static async Task Run(string output)
    {
        var results=new List<object>();
        void Check(string name,Action action){try{action();results.Add(new{name,passed=true});}catch(Exception error){results.Add(new{name,passed=false,error=error.Message});}}
        static void Assert(bool valid){if(!valid)throw new Exception("Assertion failed");}
        byte[] data=System.Text.Encoding.UTF8.GetBytes("inert-test-content-not-an-installer");
        string sha=Convert.ToHexString(SHA256.HashData(data));
        object Release(string tag="native-v0.9.7",bool draft=false,string? url=null,string? hash=null,long? size=null)=>new{tag_name=tag,draft,prerelease=true,assets=new[]{new{name="Open-Revo-0.9.7-Setup-x64.exe",browser_download_url=url??$"https://github.com/{ReleaseUpdater.Repository}/releases/download/native-v0.9.7/Open-Revo-0.9.7-Setup-x64.exe",size=size??data.Length,digest="sha256:"+(hash??sha)}}};
        string Json(params object[] values)=>JsonSerializer.Serialize(values);
        var release=ReleaseUpdater.ParseReleases(Json(Release()),new Version(0,9,6))!;
        Check("Native prerelease is selected",()=>Assert(release.Version==new Version(0,9,7)));
        Check("Current version never reinstalls",()=>Assert(ReleaseUpdater.ParseReleases(Json(Release()),new Version(0,9,7))==null));
        object PublicRelease(string version)=>new{tag_name="native-v"+version,draft=false,prerelease=true,assets=new[]{new{name=$"Open-Revo-{version}-Setup-x64.exe",browser_download_url=$"https://github.com/{ReleaseUpdater.Repository}/releases/download/native-v{version}/Open-Revo-{version}-Setup-x64.exe",size=data.Length,digest="sha256:"+sha}}};
        Check("Public 0.6.10 follows 0.6.9 numerically",()=>Assert(ReleaseUpdater.ParseReleases(Json(PublicRelease("0.6.8"),PublicRelease("0.6.9"),PublicRelease("0.6.10")),new Version(0,6,9))?.Version==new Version(0,6,10)));
        Check("Draft and upstream tag are ignored",()=>Assert(ReleaseUpdater.ParseReleases(Json(Release(draft:true),Release("v0.9.7")),new Version(0,9,6))==null));
        foreach(var (name,item) in new[]{("foreign URL",Release(url:"https://example.com/setup.exe")),("bad hash",Release(hash:"bad")),("oversize",Release(size:ReleaseUpdater.MaximumInstallerSize+1))})
            Check("Reject "+name,()=>{try{ReleaseUpdater.ParseReleases(Json(item),new Version(0,9,6));throw new Exception("accepted");}catch(IOException){}});
        string directory=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!,"fixtures",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        async Task DownloadCheck(string name,byte[] bytes,bool expectSuccess,bool cancel=false)
        {
            try
            {
                using var http=new HttpClient(new Fixture(bytes));
                using var stop=new CancellationTokenSource();if(cancel)stop.Cancel();
                try
                {
                    var actual=await new ReleaseUpdater(http).DownloadAsync(release,directory,null,stop.Token);
                    Assert(expectSuccess&&File.ReadAllBytes(actual.Path).SequenceEqual(data));
                }
                catch(Exception error) when(error is IOException or OperationCanceledException){Assert(!expectSuccess);}
                results.Add(new{name,passed=true});
            }catch(Exception error){results.Add(new{name,passed=false,error=error.Message});}
        }
        await DownloadCheck("Valid download verifies before promotion",data,true);
        await DownloadCheck("Corrupt download never promotes",new byte[data.Length],false);
        await DownloadCheck("Truncated download never promotes",data[..^1],false);
        await DownloadCheck("Overlong download never promotes",data.Concat(new byte[]{0}).ToArray(),false);
        await DownloadCheck("Cancellation never promotes",data,false,true);
        Check("No partial files remain",()=>Assert(!Directory.EnumerateFiles(directory,"*.partial",SearchOption.AllDirectories).Any()));
        Check("Installer helper quotes paths and waits parent",()=>{
            string script=ReleaseUpdater.BuildInstallScript(new(release,@"C:\test's folder\setup.exe"),@"C:\target's folder",123,456);
            Assert(script.Contains("test''s folder")&&script.Contains("target''s folder")&&script.Contains("WaitForExit(120000)")&&script.Contains("ComputeHash($stream)")&&script.Contains("$stream.Dispose()")&&script.Contains("$sha.Dispose()")&&!script.Contains("Get-FileHash")&&!script.Contains("Stop-Process")&&script.Contains("/NOCLOSEAPPLICATIONS"));
        });
        // Test the helper's PowerShell logic with Start-Process replaced by a
        // fixture. This never executes an installer or closes a real process.
        string inert=Path.Combine(directory,"fixture.exe");File.WriteAllBytes(inert,data);
        string helper=ReleaseUpdater.BuildInstallScript(new(release,inert),directory,int.MaxValue,0);
        string mock="function Start-Process { param($FilePath,$ArgumentList,[switch]$PassThru,[switch]$Wait,$WindowStyle) Set-Content -LiteralPath '"+Path.Combine(directory,"mock-start.txt").Replace("'","''")+"' -Value $ArgumentList; [pscustomobject]@{ExitCode=0} }\n";
        File.WriteAllText(Path.ChangeExtension(output,"helper.ps1"),mock+helper);
        // Retain only inert fixture files under a uniquely named temp folder.
        bool passed=results.All(r=>JsonSerializer.SerializeToElement(r).GetProperty("passed").GetBoolean());
        File.WriteAllText(output,JsonSerializer.Serialize(new{allPassed=passed,installerLaunches=0,checks=results},new JsonSerializerOptions{WriteIndented=true}));
        Environment.ExitCode=passed?0:1;
    }
    private sealed class Fixture(byte[] content):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){token.ThrowIfCancellationRequested();return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(content)});}
    }
}
