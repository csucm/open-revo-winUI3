using System.Reflection;
using OpenRevo.Core;
using OpenRevo.ResidentProtocol;

namespace OpenRevo.Resident;

internal sealed class ResidentUpdates
{
    private readonly object gate = new();
    private readonly Version version;
    private readonly string? installationDirectory;
    private readonly Action changed;
    private readonly Action verified;
    private PanelUpdateSnapshot state;
    private NativeRelease? available;
    private DownloadedUpdate? pendingInstall;
    private CancellationTokenSource? operation;
    private Task work = Task.CompletedTask;
    private bool closing;

    internal ResidentUpdates(string? installationDirectory, Action changed, Action verified)
    {
        this.installationDirectory = installationDirectory;
        this.changed = changed;
        this.verified = verified;
        string? label = typeof(ResidentUpdates).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "PublicReleaseVersion")?.Value;
        if (!Version.TryParse(label, out var parsed) || parsed.Build < 0)
            throw new InvalidOperationException("后台缺少有效的公开发布版本。");
        version = parsed;
        state = new(version.ToString(3), installationDirectory != null, PanelUpdatePhase.Idle);
    }

    internal PanelUpdateSnapshot Snapshot => Volatile.Read(ref state);

    internal PanelOutcome Check()
    {
        lock (gate)
        {
            if (closing || operation != null || pendingInstall != null) return PanelOutcome.Busy;
            available = null;
            Start(PanelUpdatePhase.Checking, CheckAsync);
            return PanelOutcome.CommandSent;
        }
    }

    internal PanelOutcome Download()
    {
        lock (gate)
        {
            if (closing || operation != null || pendingInstall != null) return PanelOutcome.Busy;
            if (installationDirectory == null || available is not { } release) return PanelOutcome.Rejected;
            Start(PanelUpdatePhase.Downloading, token => DownloadAsync(release, token));
            return PanelOutcome.CommandSent;
        }
    }

    internal PanelOutcome Cancel()
    {
        lock (gate)
        {
            if (closing) return PanelOutcome.Busy;
            if (pendingInstall != null)
            {
                pendingInstall = null;
                Publish(PanelUpdatePhase.Cancelled);
                return PanelOutcome.Applied;
            }
            if (operation is { } active)
            {
                active.Cancel();
                return PanelOutcome.CommandSent;
            }
            return PanelOutcome.Rejected;
        }
    }

    internal Task Stop()
    {
        lock (gate)
        {
            closing = true;
            operation?.Cancel();
            pendingInstall = null;
            return work;
        }
    }

    internal DownloadedUpdate? ClaimInstaller()
    {
        lock (gate)
        {
            if (closing || pendingInstall == null) return null;
            var claimed = pendingInstall;
            pendingInstall = null;
            closing = true;
            return claimed;
        }
    }

    internal string OpenDirectory()
    {
        if (installationDirectory != null) return StartupRegistration.PrepareResidentUpdateCache(installationDirectory);
        string path = UserDataStore.ValidateDirectory(Path.Combine(AppData.DirectoryPath, "updates"));
        Directory.CreateDirectory(path);
        return path;
    }

    internal void StartInstallerAfterExit(DownloadedUpdate installer)
    {
        string directory = installationDirectory ?? throw new InvalidOperationException("当前没有有效的安装授权。");
        StartupRegistration.ValidateResidentUpdatePayload(directory, installer.Path);
        ReleaseUpdater.StartInstallerAfterExit(installer, directory);
    }

    private void Start(PanelUpdatePhase phase, Func<CancellationToken, Task> action)
    {
        operation = new();
        var current = operation;
        Publish(phase, phase == PanelUpdatePhase.Downloading ? 0 : null);
        work = Task.Run(async () =>
        {
            try { await action(current.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (current.IsCancellationRequested)
            {
                lock (gate) { if (!closing) Publish(PanelUpdatePhase.Cancelled); }
            }
            catch (Exception error)
            {
                DiagnosticLog.Failure("resident.update.failed", error, new { phase });
                lock (gate) { if (!closing) Publish(PanelUpdatePhase.Failed, message: DiagnosticJournal.Scrub(error.Message, 512)); }
            }
            finally
            {
                lock (gate)
                {
                    operation = null;
                    current.Dispose();
                    changed();
                }
            }
        });
    }

    private async Task CheckAsync(CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var release = await new ReleaseUpdater(http).CheckAsync(version, token).ConfigureAwait(false);
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            available = release;
            Publish(release == null ? PanelUpdatePhase.Current : PanelUpdatePhase.Available);
        }
    }

    private async Task DownloadAsync(NativeRelease release, CancellationToken token)
    {
        string cache = OpenDirectory();
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        var download = await new ReleaseUpdater(http).DownloadAsync(release, cache, new UpdateProgress(this), token).ConfigureAwait(false);
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            pendingInstall = download;
            Publish(PanelUpdatePhase.Verified, 100);
        }
        DiagnosticLog.Record("resident.update.verified", new { version = release.Version.ToString(3), release.Size, release.Sha256 });
        verified();
    }

    private sealed class UpdateProgress(ResidentUpdates owner) : IProgress<double>
    {
        public void Report(double value)
        {
            int percent = Math.Clamp((int)(value * 100), 0, 99);
            lock (owner.gate)
                if (!owner.closing && percent > owner.state.Percent.GetValueOrDefault())
                    owner.Publish(PanelUpdatePhase.Downloading, percent);
        }
    }

    private void Publish(PanelUpdatePhase phase, int? percent = null, string? message = null)
    {
        Volatile.Write(ref state, state with
        {
            Phase = phase, AvailableVersion = available?.Version.ToString(3), Size = available?.Size, Percent = percent, Message = message
        });
        changed();
    }
}
