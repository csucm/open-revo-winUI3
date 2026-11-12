using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenRevo.Core;

public sealed record NativeRelease(Version Version, string Tag, Uri Installer, long Size, string Sha256);
public sealed record DownloadedUpdate(NativeRelease Release, string Path);

public sealed class ReleaseUpdater
{
    public const string Repository = "csucm/open-revo-winUI3";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";
    public const long MaximumInstallerSize = 512L * 1024 * 1024;
    private readonly HttpClient client;
    public ReleaseUpdater(HttpClient client) => this.client = client;

    // The installed 0.7.0 updater accepts an explicit zero revision and treats
    // it as newer than an omitted revision. Keep that identity after upgrading.
    public static Version InstalledUpdateVersion(Version publicVersion) =>
        publicVersion == new Version(0, 7, 0) ? new Version(0, 7, 0, 0) : publicVersion;

    public static NativeRelease? ParseReleases(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new IOException("发布信息格式无效。");
        JsonElement? latest = null; Version? version = null;
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()) continue;
            string tag = release.GetProperty("tag_name").GetString() ?? "";
            // Native community releases currently use GitHub's prerelease flag.
            if (!tag.StartsWith("native-v", StringComparison.Ordinal) || !Version.TryParse(tag[8..], out var candidate)) continue;
            if (candidate.Build < 0 || candidate.Revision > 0) continue;
            if (candidate <= current || version != null && candidate <= version) continue;
            latest = release; version = candidate;
        }
        if (latest == null || version == null) return null;
        var selected = latest.Value;
        string expectedName = $"Open-Revo-{version.ToString(3)}-Setup-x64.exe";
        var assets = selected.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == expectedName).ToArray();
        if (assets.Length != 1) throw new IOException("新版缺少唯一的 x64 安装包，请查看发布页。");
        var asset = assets[0]; string releaseTag = selected.GetProperty("tag_name").GetString()!;
        string expectedUrl = $"https://github.com/{Repository}/releases/download/{releaseTag}/{expectedName}";
        if (asset.GetProperty("browser_download_url").GetString() != expectedUrl) throw new IOException("安装包地址不属于本软件发布源。");
        long size = asset.GetProperty("size").GetInt64();
        string digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
        if (size <= 0 || size > MaximumInstallerSize || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit))
            throw new IOException("安装包长度或 SHA256 信息不完整，已停止更新。");
        return new(version, releaseTag, new Uri(expectedUrl), size, digest[7..]);
    }

    public async Task<NativeRelease?> CheckAsync(Version current, CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases?per_page=100");
        request.Headers.UserAgent.ParseAdd("OpenRevo-Native/" + current);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests)
            throw new IOException("GitHub 暂时限制更新检查，请稍后重试或打开发布页。无需提供账号令牌。");
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(4 * 1024 * 1024, token);
        return ParseReleases(await response.Content.ReadAsStringAsync(token), current);
    }

    public async Task<DownloadedUpdate> DownloadAsync(NativeRelease release, string cache, IProgress<double>? progress = null, CancellationToken token = default)
    {
        // Revalidate even when the candidate comes from a non-UI caller.
        string expected = $"https://github.com/{Repository}/releases/download/{release.Tag}/Open-Revo-{release.Version.ToString(3)}-Setup-x64.exe";
        if (release.Installer.AbsoluteUri != expected || release.Size <= 0 || release.Size > MaximumInstallerSize || release.Sha256.Length != 64 || !release.Sha256.All(Uri.IsHexDigit)) throw new IOException("更新目标无效。");
        string directory = System.IO.Path.Combine(cache, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string partial = System.IO.Path.Combine(directory, "installer.partial");
        string destination = System.IO.Path.Combine(directory, "Open-Revo-Setup-x64.exe");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, release.Installer);
            request.Headers.UserAgent.ParseAdd("OpenRevo-Native-Updater/" + release.Version.ToString(3));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != release.Size) throw new IOException("下载长度与发布信息不符。");
            await using var input = await response.Content.ReadAsStreamAsync(token);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0; byte[] buffer = new byte[65536];
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, true))
            {
                int count;
                while ((count = await input.ReadAsync(buffer, token)) != 0)
                {
                    total += count; if (total > release.Size) throw new IOException("下载超过声明长度。");
                    await output.WriteAsync(buffer.AsMemory(0, count), token); hash.AppendData(buffer, 0, count);
                    progress?.Report((double)total / release.Size);
                }
            }
            if (total != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("安装包 SHA256 校验失败，未运行安装程序。");
            File.Move(partial, destination);
            return new(release, destination);
        }
        catch { if (File.Exists(partial)) File.Delete(partial); throw; }
    }

    public static void StartInstallerAfterExit(DownloadedUpdate update, string installDirectory)
    {
        installDirectory = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(installDirectory));
        if (!File.Exists(System.IO.Path.Combine(installDirectory, "unins000.exe"))) throw new IOException("当前为源码预览。请先安装社区版，再使用自动安装更新。");
        // The helper lives outside the installed files, verifies again after the
        // parent exits, and never force-closes this or another running instance.
        string script = BuildInstallScript(update, installDirectory, Environment.ProcessId, Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks);
        var start = new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("无法启动更新安装助手。");
    }

    public static string BuildInstallScript(DownloadedUpdate update, string directory, int pid, long startedTicks)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        return $$"""
            $ErrorActionPreference='Stop'
            $installer={{Quote(update.Path)}}
            $target={{Quote(directory)}}
            $log=Join-Path (Split-Path -LiteralPath $installer) 'install.log'
            try {
              $parent=Get-Process -Id {{pid}} -ErrorAction SilentlyContinue
              if($parent -and $parent.StartTime.ToUniversalTime().Ticks -eq {{startedTicks}}) {
                if(-not $parent.WaitForExit(120000)) { throw 'Application did not exit. Update cancelled.' }
              }
              if((Get-Item -LiteralPath $installer).Length -ne {{update.Release.Size}}) { throw 'Installer size mismatch.' }
              $sha=[System.Security.Cryptography.SHA256]::Create()
              try {
                $stream=[System.IO.File]::OpenRead($installer)
                try { $actualHash=([System.BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','') }
                finally { $stream.Dispose() }
              } finally { $sha.Dispose() }
              if($actualHash -ne {{Quote(update.Release.Sha256)}}) { throw 'Installer SHA256 mismatch.' }
              $arguments='/SILENT /SUPPRESSMSGBOXES /NORESTART /NOCLOSEAPPLICATIONS /DIR="'+$target+'" /LOG="'+$log+'"'
              $setup=Start-Process -FilePath $installer -ArgumentList $arguments -PassThru -Wait -WindowStyle Hidden
              if($setup.ExitCode -ne 0) { throw ('Setup exit code: '+$setup.ExitCode) }
            } catch { $_ | Out-String | Add-Content -LiteralPath ($log+'.error.txt'); exit 1 }
            """;
    }
}
