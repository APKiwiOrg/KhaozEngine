using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Updates;
using Xunit;

namespace KhaozEngine.Tests.Updates;

/// <summary>
/// Covers the composed startup gate <see cref="UpdateService.EnsureUpToDateAsync"/>: its decision logic with no
/// real network or relaunch (a fake source + launch/exit hooks). Mirrors <see cref="UpdateServiceTests"/>'s setup.
/// </summary>
public sealed class UpdateGateTests : IDisposable
{
    private readonly string root;
    private readonly string installDir;
    private readonly string appDataDir;
    private readonly FakeUpdateSource source = new();
    private readonly List<(string updaterPath, string configPath)> launches = new();
    private bool exited;

    private static readonly System.Security.Cryptography.RSA SignKey = System.Security.Cryptography.RSA.Create(2048);
    private static string PrivPem => SignKey.ExportRSAPrivateKeyPem();
    private static string PubPem => SignKey.ExportSubjectPublicKeyInfoPem();
    private const string ManifestUrl = "https://u.example.com/2.0.0/manifest.json";

    public UpdateGateTests()
    {
        root = Path.Combine(Path.GetTempPath(), "ke-updates-gate-" + Guid.NewGuid().ToString("N"));
        installDir = Path.Combine(root, "install");
        appDataDir = Path.Combine(root, "appdata");
        Directory.CreateDirectory(installDir);
        Directory.CreateDirectory(appDataDir);
        // ApplyUpdate requires the updater shim to exist on disk before it launches it (via the test hook).
        string updaterName = OperatingSystem.IsWindows() ? "TestUpdater.exe" : "TestUpdater";
        File.WriteAllText(Path.Combine(installDir, updaterName), "shim");
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private UpdateService Build(string currentVersion = "1.0.0", IUpdateSource? src = null)
        => new(new UpdateServiceOptions
        {
            Source = src ?? source,
            CurrentVersion = currentVersion,
            TrustedPublicKeys = new[] { PubPem },
            InstallDir = installDir,
            AppDataDir = appDataDir,
            Platform = "win-x64",
            UpdaterExecutableName = "TestUpdater",
            DisposeSource = false,
            LaunchUpdater = (u, c) => { launches.Add((u, c)); return true; },
            ExitProcess = () => exited = true,
        });

    private void SetupV2Available(bool required = false)
    {
        File.WriteAllText(Path.Combine(installDir, "game.dll"), "v1");
        string gameSha = source.Add("game.dll", "v2");
        string newSha = source.Add("new.dll", "n1");
        var remote = new UpdateManifest { Version = "2.0.0", Platform = "win-x64" };
        remote.Files.Add(new ManifestFileEntry { Path = "game.dll", Sha256 = gameSha, Size = 2 });
        remote.Files.Add(new ManifestFileEntry { Path = "new.dll", Sha256 = newSha, Size = 2 });
        source.PublishSigned(remote, ManifestUrl, PrivPem, required);
    }

    [Fact]
    public async Task UpToDate_When_FeedReports_NoNewerBuild()
    {
        // Feed reachable, version not newer than current: UpToDate, no apply launched.
        source.Latest = new LatestVersionInfo("1.0.0", "1.0.0", ManifestUrl, false);
        using UpdateService svc = Build();

        UpdateGateResult result = await svc.EnsureUpToDateAsync();

        Assert.Equal(UpdateGateOutcome.UpToDate, result.Outcome);
        Assert.False(exited);
        Assert.Empty(launches);
    }

    [Fact]
    public async Task FeedUnreachable_When_FeedReturnsNothing()
    {
        // Latest stays null (transport error / down feed): non-fatal, proceed on current build.
        using UpdateService svc = Build();

        UpdateGateResult result = await svc.EnsureUpToDateAsync();

        Assert.Equal(UpdateGateOutcome.FeedUnreachable, result.Outcome);
        Assert.False(exited);
    }

    [Fact]
    public async Task FeedUnreachable_When_CheckExceedsTimeout()
    {
        // A feed that hangs past the (tiny) timeout must fall through to FeedUnreachable, not block startup.
        using UpdateService svc = Build(src: new HangingSource());

        UpdateGateResult result = await svc.EnsureUpToDateAsync(checkTimeout: TimeSpan.FromMilliseconds(50));

        Assert.Equal(UpdateGateOutcome.FeedUnreachable, result.Outcome);
    }

    [Fact]
    public async Task Updating_When_NewerBuild_DownloadsAndApplies()
    {
        SetupV2Available();
        using UpdateService svc = Build();

        UpdateGateResult result = await svc.EnsureUpToDateAsync();

        Assert.Equal(UpdateGateOutcome.Updating, result.Outcome);
        Assert.Equal("2.0.0", result.RemoteVersion);
        Assert.True(exited);              // ApplyUpdate ran the exit hook (would terminate the process in production)
        Assert.Single(launches);          // updater shim launched
    }

    [Fact]
    public async Task Updating_AppliesEvenWhenUpdateNotMarkedRequired()
    {
        // The gate self-heals before connecting regardless of the manifest's "required" flag.
        SetupV2Available(required: false);
        using UpdateService svc = Build();

        UpdateGateResult result = await svc.EnsureUpToDateAsync();

        Assert.Equal(UpdateGateOutcome.Updating, result.Outcome);
    }

    [Fact]
    public async Task Failed_When_DownloadCannotComplete()
    {
        SetupV2Available();
        source.AlwaysCorrupt.Add("game.dll");   // every download fails the SHA256 check
        using UpdateService svc = Build();

        UpdateGateResult result = await svc.EnsureUpToDateAsync();

        Assert.Equal(UpdateGateOutcome.Failed, result.Outcome);
        Assert.False(exited);                    // never applied
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Progress_Reports_DownloadingPhase()
    {
        SetupV2Available();
        using UpdateService svc = Build();
        var phases = new List<UpdateGatePhase>();
        var progress = new ImmediateProgress(p => phases.Add(p.Phase));

        await svc.EnsureUpToDateAsync(progress);

        Assert.Contains(UpdateGatePhase.Downloading, phases);
    }

    /// <summary>An <see cref="IUpdateSource"/> whose version check never returns until cancelled (simulates a hung feed).</summary>
    private sealed class HangingSource : IUpdateSource
    {
        public async Task<LatestVersionInfo?> CheckLatestVersionAsync(string platform, CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return null;
        }
        public Task<byte[]?> DownloadBytesAsync(string url, long maxBytes, CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(null);
        public Task<bool> DownloadFileAsync(string fileUrl, string destPath, long maxBytes, IProgress<long>? bytesProgress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
        public string ResolveFileUrl(LatestVersionInfo latest, string relativePath) => relativePath;
    }

    /// <summary>Synchronous <see cref="IProgress{T}"/> so reports land on the calling thread (deterministic in tests,
    /// unlike <see cref="Progress{T}"/> which posts to the captured synchronization context).</summary>
    private sealed class ImmediateProgress : IProgress<UpdateGateProgress>
    {
        private readonly Action<UpdateGateProgress> onReport;
        public ImmediateProgress(Action<UpdateGateProgress> onReport) => this.onReport = onReport;
        public void Report(UpdateGateProgress value) => onReport(value);
    }
}
