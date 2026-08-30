using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.Updates;
using Xunit;

namespace KhaozEngine.Tests.Updates;

/// <summary>
/// Covers <see cref="UpdateService.VerifyAndRepairAsync"/>: the integrity pass that hashes what is actually on
/// disk instead of trusting the recorded local manifest. The scenario the whole path exists for is
/// <see cref="Repair_CorruptFileWithStaleCachedManifest_IsDetectedAndRepaired"/>, which pins BOTH halves of the
/// original blindness in one fixture.
/// </summary>
public sealed class UpdateRepairTests : IDisposable
{
    private readonly string root;
    private readonly string installDir;
    private readonly string appDataDir;
    private readonly FakeUpdateSource source = new();
    private readonly List<(string UpdaterPath, string ConfigPath)> launches = new();
    private bool exited;

    private static readonly System.Security.Cryptography.RSA SignKey = System.Security.Cryptography.RSA.Create(2048);
    private static string PrivPem => SignKey.ExportRSAPrivateKeyPem();
    private static string PubPem => SignKey.ExportSubjectPublicKeyInfoPem();

    private const string InstalledVersion = "1.0.0";
    private const string ManifestUrl = "https://u.example.com/manifest.json";
    private const string Platform = "win-x64";

    private static readonly string UpdaterFileName =
        OperatingSystem.IsWindows() ? "TestUpdater.exe" : "TestUpdater";

    public UpdateRepairTests()
    {
        root = Path.Combine(Path.GetTempPath(), "ke-updates-repair-" + Guid.NewGuid().ToString("N"));
        installDir = Path.Combine(root, "install");
        appDataDir = Path.Combine(root, "appdata");
        Directory.CreateDirectory(installDir);
        Directory.CreateDirectory(appDataDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private UpdateService Build(string currentVersion = InstalledVersion)
        => new(new UpdateServiceOptions
        {
            Source = source,
            CurrentVersion = currentVersion,
            TrustedPublicKeys = new[] { PubPem },
            InstallDir = installDir,
            AppDataDir = appDataDir,
            Platform = Platform,
            UpdaterExecutableName = "TestUpdater",
            DisposeSource = false,
            LaunchUpdater = (u, c) => { launches.Add((u, c)); return true; },
            ExitProcess = () => exited = true,
        });

    private string StagingDir(string version) => Path.Combine(appDataDir, "update-staging", version);

    private string CachedManifestPath => Path.Combine(appDataDir, "update-manifest.json");

    private string InstallPath(string relativePath)
        => Path.Combine(installDir, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Writes a file into the install AND registers its correct bytes with the feed, returning the
    /// manifest entry that describes it.</summary>
    private ManifestFileEntry Lay(string relativePath, string content)
    {
        string full = InstallPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return new ManifestFileEntry
        {
            Path = relativePath,
            Sha256 = source.Add(relativePath, content),
            Size = Encoding.UTF8.GetByteCount(content),
        };
    }

    /// <summary>
    /// Lays down a healthy install and publishes the matching SIGNED manifest at the SAME version, i.e. exactly
    /// the "client is up to date" state the normal check short-circuits on. Also writes the cached local
    /// manifest the shim installs after an apply, so the cache records the correct hashes.
    /// </summary>
    private UpdateManifest SetupHealthyInstall(string version = InstalledVersion)
    {
        var remote = new UpdateManifest { Version = version, Platform = Platform };
        remote.Files.Add(Lay("game.dll", "the game binary"));
        remote.Files.Add(Lay("data/pack.bin", "content pack bytes"));
        remote.Files.Add(Lay(UpdaterFileName, "shim"));
        source.PublishSigned(remote, ManifestUrl, PrivPem);
        File.WriteAllText(CachedManifestPath, remote.Serialize());
        return remote;
    }

    /// <summary>Overwrites an installed file with NUL bytes: the tester's actual damage.</summary>
    private void CorruptOnDisk(string relativePath)
        => File.WriteAllBytes(InstallPath(relativePath), new byte[] { 0, 0, 0, 0 });

    private ApplyUpdateConfig ReadApplyConfig()
        => JsonSerializer.Deserialize<ApplyUpdateConfig>(
            File.ReadAllText(Path.Combine(appDataDir, "apply-update.json")))!;

    // --- The regression this whole path exists for ---

    [Fact]
    public async Task Repair_CorruptFileWithStaleCachedManifest_IsDetectedAndRepaired()
    {
        UpdateManifest remote = SetupHealthyInstall();
        CorruptOnDisk("game.dll");
        using UpdateService svc = Build();

        // Half one of the old blindness: the version gate. Local version == latest, so the normal check never
        // fetches the manifest, never hashes a byte, and reports "up to date" on a damaged install.
        await svc.CheckForUpdateAsync();
        Assert.Equal(UpdateState.Idle, svc.State);
        Assert.Equal(0, source.DownloadCalls);

        // Half two: even past that gate the local picture would be the CACHED manifest, which still records
        // game.dll's correct hash, so the diff that drives every download sees nothing wrong.
        UpdateManifest cached = UpdateManifest.Deserialize(File.ReadAllText(CachedManifestPath))!;
        Assert.Empty(UpdateManifest.ComputeDiff(cached, remote).FilesToDownload);

        UpdateRepairResult result = await svc.VerifyAndRepairAsync();

        // Hashing the real files sees it, at the same version, with the same stale cache still sitting there.
        Assert.Equal(UpdateRepairOutcome.Repairing, result.Outcome);
        Assert.Equal(InstalledVersion, result.Version);
        Assert.Equal(3, result.FilesChecked);
        Assert.Equal(1, result.FilesNeedingRepair);
        Assert.Equal(new[] { "game.dll" }, result.MismatchedFiles);
        Assert.Empty(result.MissingFiles);

        // And it really re-fetched the good bytes and handed them to the applier.
        Assert.Equal("the game binary", File.ReadAllText(Path.Combine(StagingDir(InstalledVersion), "game.dll")));
        Assert.True(exited);
        Assert.Single(launches);
        Assert.Contains("game.dll", ReadApplyConfig().FilesToCopy);
    }

    // --- Clean install ---

    [Fact]
    public async Task Repair_CleanInstall_VerifiesEverythingAndDownloadsNothing()
    {
        SetupHealthyInstall();
        using UpdateService svc = Build();

        UpdateRepairResult result = await svc.VerifyAndRepairAsync();

        Assert.Equal(UpdateRepairOutcome.Verified, result.Outcome);
        Assert.Equal(3, result.FilesChecked);
        Assert.Equal(0, result.FilesNeedingRepair);
        Assert.Empty(result.MismatchedFiles);
        Assert.Empty(result.MissingFiles);
        Assert.Empty(result.ExtraneousFiles);
        Assert.False(result.RelaunchRequired);
        Assert.Null(result.Error);
        Assert.Equal(0, source.DownloadCalls);
        Assert.Empty(launches);
        Assert.Equal(UpdateState.Idle, svc.State);
    }

    /// <summary>#164: the repair path composes the same download loop, and it builds its own download plan, so
    /// it is the entry point that actually reaches the loop's traversal guard. A validly-signed path escaping
    /// the staging dir has to fail the repair rather than write outside it.</summary>
    [Fact]
    public async Task Repair_RefusesAManifestFilePathThatEscapesStaging()
    {
        UpdateManifest remote = SetupHealthyInstall();
        // Declared by the manifest, absent from the install, so the repair diff wants to download it.
        remote.Files.Add(new ManifestFileEntry
        {
            Path = "../../pwned.dll",
            Sha256 = source.Add("../../pwned.dll", "bad"),
            Size = 3,
        });
        source.PublishSigned(remote, ManifestUrl, PrivPem);
        using UpdateService svc = Build();

        UpdateRepairResult result = await svc.VerifyAndRepairAsync();

        Assert.Equal(UpdateRepairOutcome.Failed, result.Outcome);
        Assert.Equal(0, source.DownloadCalls);
        Assert.False(File.Exists(Path.Combine(appDataDir, "pwned.dll")));
        Assert.False(File.Exists(Path.Combine(root, "pwned.dll")));
        Assert.Empty(launches);
    }

    // --- Missing file ---

    [Fact]
    public async Task Repair_MissingFile_IsDetectedAndRestored()
    {
        SetupHealthyInstall();
        File.Delete(InstallPath("data/pack.bin"));
        using UpdateService svc = Build();

        UpdateRepairResult result = await svc.VerifyAndRepairAsync();

        Assert.Equal(UpdateRepairOutcome.Repairing, result.Outcome);
        Assert.Equal(new[] { "data/pack.bin" }, result.MissingFiles);
        Assert.Empty(result.MismatchedFiles);
        Assert.Equal(1, result.FilesNeedingRepair);
        Assert.Equal("content pack bytes",
            File.ReadAllText(Path.Combine(StagingDir(InstalledVersion), "data", "pack.bin")));
        Assert.Contains("data/pack.bin", ReadApplyConfig().FilesToCopy);
    }

    // --- Extraneous files: reported, never deleted ---

    [Fact]
    public async Task Repair_ExtraneousFile_IsReportedButNeverDeleted()
    {
        SetupHealthyInstall();
        File.WriteAllText(InstallPath("crash-2026-07-26.log"), "not ours");
        CorruptOnDisk("game.dll");   // force a real repair, so an apply config is written
        using UpdateService svc = Build();

        UpdateRepairResult result = await svc.VerifyAndRepairAsync();

        Assert.Equal(UpdateRepairOutcome.Repairing, result.Outcome);
        Assert.Equal(new[] { "crash-2026-07-26.log" }, result.ExtraneousFiles);
        // The repair hands the applier NOTHING to delete: a fresh scan cannot tell a player's log, mod, or
        // screenshot from a leftover, so an extra file is reported and left exactly where it is.
        Assert.Empty(ReadApplyConfig().FilesToDelete);
        Assert.True(File.Exists(InstallPath("crash-2026-07-26.log")));
    }

    // --- Signing still gates the repair path ---

    [Fact]
    public async Task Repair_UnsignedManifest_IsRejectedAndNeverReportsVerified()
    {
        SetupHealthyInstall();
        source.Bytes.Remove(ManifestUrl + ".sig");   // manifest served, signature absent
        CorruptOnDisk("game.dll");
        using UpdateService svc = Build();

        UpdateRepairResult result = await svc.VerifyAndRepairAsync();

        // Nothing could be verified, so it must not read as a clean install, and nothing may be installed.
        Assert.Equal(UpdateRepairOutcome.FeedUnreachable, result.Outcome);
        Assert.Equal(0, source.DownloadCalls);
        Assert.Empty(launches);
        Assert.Equal(UpdateState.Idle, svc.State);
    }

    [Fact]
    public async Task Repair_TamperedManifest_IsRejected()
    {
        SetupHealthyInstall();
        // The signature was taken over the original bytes, so serve altered ones.
        source.Bytes[ManifestUrl] = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(source.Bytes[ManifestUrl]) + " ");
        CorruptOnDisk("game.dll");
        using UpdateService svc = Build();

        UpdateRepairResult result = await svc.VerifyAndRepairAsync();

        Assert.Equal(UpdateRepairOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Error);
        Assert.Equal(0, source.DownloadCalls);
        Assert.Empty(launches);
    }

    // --- Feed states ---

    [Fact]
    public async Task Repair_FeedUnreachable_DoesNotReportVerified()
    {
        SetupHealthyInstall();
        source.Latest = null;
        using UpdateService svc = Build();

        UpdateRepairResult result = await svc.VerifyAndRepairAsync();

        Assert.Equal(UpdateRepairOutcome.FeedUnreachable, result.Outcome);
        Assert.Equal(0, result.FilesChecked);
        Assert.Null(result.Version);
    }

    [Fact]
    public async Task Repair_FeedOlderThanInstall_IsRefused()
    {
        SetupHealthyInstall("0.9.0");   // published build is BEHIND the installed one
        CorruptOnDisk("game.dll");
        using UpdateService svc = Build(currentVersion: InstalledVersion);

        UpdateRepairResult result = await svc.VerifyAndRepairAsync();

        Assert.Equal(UpdateRepairOutcome.Failed, result.Outcome);
        Assert.Equal("0.9.0", result.Version);
        Assert.Equal(0, source.DownloadCalls);   // a repair never silently downgrades
    }

    [Fact]
    public async Task Repair_FeedNewerThanInstall_RepairsForwardToIt()
    {
        SetupHealthyInstall();
        // The feed has moved on: same file set, one file changed in 1.1.0.
        var newer = new UpdateManifest { Version = "1.1.0", Platform = Platform };
        newer.Files.Add(new ManifestFileEntry
        {
            Path = "game.dll",
            Sha256 = source.Add("game.dll", "the NEXT game binary"),
            Size = Encoding.UTF8.GetByteCount("the NEXT game binary"),
        });
        source.PublishSigned(newer, ManifestUrl, PrivPem);
        using UpdateService svc = Build();

        UpdateRepairResult result = await svc.VerifyAndRepairAsync();

        Assert.Equal(UpdateRepairOutcome.Repairing, result.Outcome);
        Assert.Equal("1.1.0", result.Version);
        Assert.Equal(new[] { "game.dll" }, result.MismatchedFiles);
        // The files 1.1.0 dropped are extras now, and are still reported rather than deleted.
        Assert.Contains("data/pack.bin", result.ExtraneousFiles);
        Assert.Empty(ReadApplyConfig().FilesToDelete);
        Assert.Equal("the NEXT game binary", File.ReadAllText(Path.Combine(StagingDir("1.1.0"), "game.dll")));
    }

    // --- Deferred apply ---

    [Fact]
    public async Task Repair_ApplyDeferred_StagesRepairAndRequiresRelaunch()
    {
        SetupHealthyInstall();
        CorruptOnDisk("game.dll");
        using UpdateService svc = Build();

        UpdateRepairResult result = await svc.VerifyAndRepairAsync(applyRepair: false);

        Assert.Equal(UpdateRepairOutcome.RepairStaged, result.Outcome);
        Assert.True(result.RelaunchRequired);
        Assert.Equal(1, result.FilesNeedingRepair);
        Assert.False(exited);
        Assert.Empty(launches);
        Assert.Equal(UpdateState.ReadyToApply, svc.State);
        Assert.True(File.Exists(Path.Combine(StagingDir(InstalledVersion), "game.dll")));

        // The caller finishes it whenever it likes, through the ordinary apply.
        Assert.True(svc.ApplyUpdate());
        Assert.Single(launches);
    }

    [Fact]
    public async Task Repair_StagedFileFromAnEarlierAttempt_IsNotRedownloaded()
    {
        SetupHealthyInstall();
        CorruptOnDisk("game.dll");
        using UpdateService svc = Build();
        Directory.CreateDirectory(StagingDir(InstalledVersion));
        File.WriteAllText(Path.Combine(StagingDir(InstalledVersion), "game.dll"), "the game binary");

        UpdateRepairResult result = await svc.VerifyAndRepairAsync(applyRepair: false);

        Assert.Equal(UpdateRepairOutcome.RepairStaged, result.Outcome);
        Assert.Equal(1, result.FilesNeedingRepair);   // still reported as damaged
        Assert.Equal(0, source.DownloadCalls);        // but the intact staged copy is reused
    }

    // --- Progress + state guard ---

    [Fact]
    public async Task Repair_ReportsHashingThenDownloadProgress()
    {
        SetupHealthyInstall();
        CorruptOnDisk("game.dll");
        using UpdateService svc = Build();
        var reports = new List<UpdateRepairProgress>();

        await svc.VerifyAndRepairAsync(new SyncProgress(reports.Add));

        List<UpdateRepairProgress> hashing =
            reports.FindAll(r => r.Phase == UpdateRepairPhase.Verifying && r.TotalFiles > 0);
        Assert.NotEmpty(hashing);
        Assert.Equal(3, hashing[0].TotalFiles);                       // every installed file is hashed
        Assert.Equal(3, hashing[^1].FilesDone);
        Assert.Equal(hashing[^1].TotalBytes, hashing[^1].BytesDone);
        Assert.Contains(reports, r => r.Phase == UpdateRepairPhase.Downloading);
        Assert.Contains(reports, r => r.Phase == UpdateRepairPhase.Applying);
    }

    [Fact]
    public async Task Repair_WhileAnApplyIsInFlight_IsRefused()
    {
        // Drive a normal update to the Applying state first.
        File.WriteAllText(InstallPath(UpdaterFileName), "shim");
        var newer = new UpdateManifest { Version = "2.0.0", Platform = Platform };
        newer.Files.Add(new ManifestFileEntry
        {
            Path = "game.dll",
            Sha256 = source.Add("game.dll", "v2"),
            Size = 2,
        });
        source.PublishSigned(newer, ManifestUrl, PrivPem);
        using UpdateService svc = Build();
        await svc.CheckForUpdateAsync();
        await svc.StartDownloadAsync();
        Assert.True(svc.ApplyUpdate());
        Assert.Equal(UpdateState.Applying, svc.State);

        UpdateRepairResult result = await svc.VerifyAndRepairAsync();

        Assert.Equal(UpdateRepairOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Error);
        Assert.Equal(UpdateState.Applying, svc.State);   // the in-flight apply is left alone
    }

    /// <summary>Synchronous progress sink: <see cref="Progress{T}"/> posts asynchronously, which would race
    /// the assertions.</summary>
    private sealed class SyncProgress : IProgress<UpdateRepairProgress>
    {
        private readonly Action<UpdateRepairProgress> onReport;
        public SyncProgress(Action<UpdateRepairProgress> onReport) => this.onReport = onReport;
        public void Report(UpdateRepairProgress value) => onReport(value);
    }
}
