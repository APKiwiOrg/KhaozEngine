using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.Updates;
using Xunit;

namespace KhaozEngine.Tests.Updates;

public sealed class UpdateServiceTests : IDisposable
{
    private readonly string root;
    private readonly string installDir;
    private readonly string appDataDir;
    private readonly FakeUpdateSource source = new();
    private readonly List<(string updaterPath, string configPath)> launches = new();
    private bool exited;

    // Shared signing key for the test suite. The signer holds the private key; the service trusts the
    // matching public key. Sign with PrivPem, trust PubPem.
    private static readonly System.Security.Cryptography.RSA SignKey = System.Security.Cryptography.RSA.Create(2048);
    private static string PrivPem => SignKey.ExportRSAPrivateKeyPem();
    private static string PubPem => SignKey.ExportSubjectPublicKeyInfoPem();

    private const string ManifestUrl = "https://u.example.com/2.0.0/manifest.json";

    public UpdateServiceTests()
    {
        root = Path.Combine(Path.GetTempPath(), "ke-updates-svc-" + Guid.NewGuid().ToString("N"));
        installDir = Path.Combine(root, "install");
        appDataDir = Path.Combine(root, "appdata");
        Directory.CreateDirectory(installDir);
        Directory.CreateDirectory(appDataDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private static readonly string UpdaterFileName =
        OperatingSystem.IsWindows() ? "TestUpdater.exe" : "TestUpdater";

    private UpdateService Build(string currentVersion = "1.0.0", int maxRetries = 2,
        IReadOnlyList<string>? trustedKeys = null, long? maxFileBytes = null,
        UpdaterUiOptions? updaterUi = null)
        => new(new UpdateServiceOptions
        {
            Source = source,
            CurrentVersion = currentVersion,
            TrustedPublicKeys = trustedKeys ?? new[] { PubPem },
            InstallDir = installDir,
            AppDataDir = appDataDir,
            Platform = "win-x64",
            UpdaterExecutableName = "TestUpdater",
            MaxDownloadRetries = maxRetries,
            MaxFileBytes = maxFileBytes ?? 4L * 1024 * 1024 * 1024,
            DisposeSource = false,
            LaunchUpdater = (u, c) => { launches.Add((u, c)); return true; },
            ExitProcess = () => exited = true,
            UpdaterUi = updaterUi
        });

    private string StagingDir(string version) => Path.Combine(appDataDir, "update-staging", version);

    /// <summary>
    /// Sets up a standard "v2 available" scenario: install has game.dll@v1; remote changes game.dll
    /// and adds new.dll. Publishes the remote manifest SIGNED with the trusted key.
    /// </summary>
    private void SetupV2Available()
    {
        File.WriteAllText(Path.Combine(installDir, "game.dll"), "v1");

        string gameSha = source.Add("game.dll", "v2");
        string newSha = source.Add("new.dll", "n1");

        var remote = new UpdateManifest { Version = "2.0.0", Platform = "win-x64" };
        remote.Files.Add(new ManifestFileEntry { Path = "game.dll", Sha256 = gameSha, Size = 2 });
        remote.Files.Add(new ManifestFileEntry { Path = "new.dll", Sha256 = newSha, Size = 2 });
        source.PublishSigned(remote, ManifestUrl, PrivPem);
    }

    /// <summary>Like <see cref="SetupV2Available"/> but the SIGNED manifest is marked required.</summary>
    private void SetupRequiredV2Available()
    {
        File.WriteAllText(Path.Combine(installDir, "game.dll"), "v1");

        string gameSha = source.Add("game.dll", "v2");
        string newSha = source.Add("new.dll", "n1");

        var remote = new UpdateManifest { Version = "2.0.0", Platform = "win-x64", Required = true };
        remote.Files.Add(new ManifestFileEntry { Path = "game.dll", Sha256 = gameSha, Size = 2 });
        remote.Files.Add(new ManifestFileEntry { Path = "new.dll", Sha256 = newSha, Size = 2 });
        source.PublishSigned(remote, ManifestUrl, PrivPem, required: true);
    }

    [Fact]
    public async Task AutoAdvanceRequired_RequiredUpdate_DownloadsAndApplies_WithNoKeypress()
    {
        SetupRequiredV2Available();
        File.WriteAllText(Path.Combine(installDir, UpdaterFileName), "shim");
        using UpdateService svc = Build();

        await svc.CheckForUpdateAsync();
        Assert.Equal(UpdateState.UpdateAvailable, svc.State);
        Assert.True(svc.IsRequired);

        // Simulate the game loop driving the policy each frame: NO player keypress at all.
        for (int frame = 0; frame < 8 && !exited; frame++)
        {
            UpdateOverlayActions.AutoAdvanceRequired(svc);
        }

        Assert.True(exited);
        Assert.Single(launches);
        Assert.Equal(UpdateState.Applying, svc.State);
    }

    [Fact]
    public async Task AutoAdvanceRequired_PreStagedRequiredUpdate_AppliesImmediately()
    {
        // Install: game.dll@v1 + the shim. Remote (required): game.dll@v2, new.dll, and the shim UNCHANGED,
        // so there is no phantom delete; with both changed files pre-staged the check lands directly on
        // ReadyToApply and a single frame of the policy applies it.
        File.WriteAllText(Path.Combine(installDir, "game.dll"), "v1");
        File.WriteAllText(Path.Combine(installDir, UpdaterFileName), "shim");
        string gameSha = source.Add("game.dll", "v2");
        string newSha = source.Add("new.dll", "n1");
        var remote = new UpdateManifest { Version = "2.0.0", Platform = "win-x64", Required = true };
        remote.Files.Add(new ManifestFileEntry { Path = "game.dll", Sha256 = gameSha, Size = 2 });
        remote.Files.Add(new ManifestFileEntry { Path = "new.dll", Sha256 = newSha, Size = 2 });
        remote.Files.Add(new ManifestFileEntry { Path = UpdaterFileName, Sha256 = FakeUpdateSource.Sha("shim"), Size = 4 });
        source.PublishSigned(remote, ManifestUrl, PrivPem, required: true);

        using UpdateService svc = Build();
        Directory.CreateDirectory(StagingDir("2.0.0"));
        File.WriteAllText(Path.Combine(StagingDir("2.0.0"), "game.dll"), "v2");
        File.WriteAllText(Path.Combine(StagingDir("2.0.0"), "new.dll"), "n1");

        await svc.CheckForUpdateAsync();
        Assert.Equal(UpdateState.ReadyToApply, svc.State);

        UpdateOverlayActions.AutoAdvanceRequired(svc);

        Assert.True(exited);
        Assert.Single(launches);
    }

    [Fact]
    public async Task AutoAdvanceRequired_OptionalUpdate_DoesNothing()
    {
        SetupV2Available();
        File.WriteAllText(Path.Combine(installDir, UpdaterFileName), "shim");
        using UpdateService svc = Build();

        await svc.CheckForUpdateAsync();
        Assert.Equal(UpdateState.UpdateAvailable, svc.State);
        Assert.False(svc.IsRequired);

        UpdateOverlayActions.AutoAdvanceRequired(svc);

        // Optional updates stay player-driven: no auto-download, no apply.
        Assert.Equal(UpdateState.UpdateAvailable, svc.State);
        Assert.False(exited);
    }

    [Fact]
    public async Task AutoAdvanceRequired_FailedRequiredUpdate_DoesNotAutoRetry()
    {
        SetupRequiredV2Available();
        source.AlwaysCorrupt.Add("game.dll");
        File.WriteAllText(Path.Combine(installDir, UpdaterFileName), "shim");
        using UpdateService svc = Build();

        await svc.CheckForUpdateAsync();
        // Drive to a failed download.
        for (int frame = 0; frame < 8 && svc.State != UpdateState.Failed && !exited; frame++)
        {
            UpdateOverlayActions.AutoAdvanceRequired(svc);
        }
        Assert.Equal(UpdateState.Failed, svc.State);

        int downloadsBefore = source.DownloadCalls;
        // Further frames must NOT hot-loop retrying (the player keeps the keypress-retry path).
        for (int frame = 0; frame < 5; frame++)
        {
            UpdateOverlayActions.AutoAdvanceRequired(svc);
        }

        Assert.Equal(UpdateState.Failed, svc.State);
        Assert.Equal(downloadsBefore, source.DownloadCalls);
    }

    [Fact]
    public async Task Check_UpdateAvailable_ListsChangedFiles()
    {
        SetupV2Available();
        using UpdateService svc = Build();

        await svc.CheckForUpdateAsync();

        Assert.Equal(UpdateState.UpdateAvailable, svc.State);
        Assert.Equal("2.0.0", svc.RemoteVersion);
        Assert.Equal(2, svc.TotalFilesToDownload);
        Assert.Equal(4, svc.TotalDownloadBytes);
    }

    [Fact]
    public async Task Check_UpToDate_GoesIdle()
    {
        SetupV2Available();
        using UpdateService svc = Build(currentVersion: "2.0.0");

        await svc.CheckForUpdateAsync();

        Assert.Equal(UpdateState.Idle, svc.State);
    }

    [Fact]
    public async Task Check_Offline_GoesIdle()
    {
        source.Latest = null;
        using UpdateService svc = Build();

        await svc.CheckForUpdateAsync();

        Assert.Equal(UpdateState.Idle, svc.State);
    }

    [Fact]
    public async Task Resume_ValidStagedFile_IsSkipped()
    {
        SetupV2Available();
        using UpdateService svc = Build();
        // Stage new.dll correctly (after ctor cleanup has run).
        Directory.CreateDirectory(StagingDir("2.0.0"));
        File.WriteAllText(Path.Combine(StagingDir("2.0.0"), "new.dll"), "n1");

        await svc.CheckForUpdateAsync();

        Assert.Equal(UpdateState.UpdateAvailable, svc.State);
        Assert.Equal(1, svc.TotalFilesToDownload); // only game.dll remains
    }

    [Fact]
    public async Task Resume_CorruptStagedFile_IsNotSkipped()
    {
        SetupV2Available();
        using UpdateService svc = Build();
        Directory.CreateDirectory(StagingDir("2.0.0"));
        File.WriteAllText(Path.Combine(StagingDir("2.0.0"), "new.dll"), "WRONG");

        await svc.CheckForUpdateAsync();

        Assert.Equal(2, svc.TotalFilesToDownload); // corrupt staged file does not count
    }

    [Fact]
    public async Task Check_AllStaged_GoesReadyToApply()
    {
        SetupV2Available();
        using UpdateService svc = Build();
        Directory.CreateDirectory(StagingDir("2.0.0"));
        File.WriteAllText(Path.Combine(StagingDir("2.0.0"), "game.dll"), "v2");
        File.WriteAllText(Path.Combine(StagingDir("2.0.0"), "new.dll"), "n1");

        await svc.CheckForUpdateAsync();

        Assert.Equal(UpdateState.ReadyToApply, svc.State);
    }

    [Fact]
    public async Task Download_Succeeds_StagesFilesAndManifest()
    {
        SetupV2Available();
        using UpdateService svc = Build();

        await svc.CheckForUpdateAsync();
        await svc.StartDownloadAsync();

        Assert.Equal(UpdateState.ReadyToApply, svc.State);
        Assert.Equal(2, svc.FilesDownloaded);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(StagingDir("2.0.0"), "game.dll")));
        Assert.Equal("n1", File.ReadAllText(Path.Combine(StagingDir("2.0.0"), "new.dll")));
        string stagedManifest = Path.Combine(StagingDir("2.0.0"), "manifest.json");
        Assert.True(File.Exists(stagedManifest));
        Assert.Contains("2.0.0", File.ReadAllText(stagedManifest));
    }

    [Fact]
    public async Task Download_AlwaysCorrupt_Fails()
    {
        SetupV2Available();
        source.AlwaysCorrupt.Add("game.dll");
        using UpdateService svc = Build();

        await svc.CheckForUpdateAsync();
        await svc.StartDownloadAsync();

        Assert.Equal(UpdateState.Failed, svc.State);
        Assert.NotNull(svc.ErrorMessage);
    }

    [Fact]
    public async Task Download_CorruptFirstAttempt_RetriesAndSucceeds()
    {
        SetupV2Available();
        source.CorruptFirstAttempt.Add("game.dll");
        using UpdateService svc = Build(maxRetries: 2);

        await svc.CheckForUpdateAsync();
        await svc.StartDownloadAsync();

        Assert.Equal(UpdateState.ReadyToApply, svc.State);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(StagingDir("2.0.0"), "game.dll")));
    }

    [Fact]
    public async Task Apply_WritesConfigAndLaunchesShim()
    {
        SetupV2Available();
        File.WriteAllText(Path.Combine(installDir, UpdaterFileName), "shim");
        using UpdateService svc = Build();

        await svc.CheckForUpdateAsync();
        await svc.StartDownloadAsync();
        bool started = svc.ApplyUpdate();

        Assert.True(started);
        Assert.True(exited);
        Assert.Single(launches);

        string applyJson = File.ReadAllText(Path.Combine(appDataDir, "apply-update.json"));
        ApplyUpdateConfig cfg = System.Text.Json.JsonSerializer.Deserialize<ApplyUpdateConfig>(applyJson)!;
        Assert.Equal("2.0.0", cfg.TargetVersion);
        Assert.Equal(installDir, cfg.InstallDir);
        Assert.Equal(StagingDir("2.0.0"), cfg.StagingDir);
        Assert.Contains("game.dll", cfg.FilesToCopy);
        Assert.Contains("new.dll", cfg.FilesToCopy);
        Assert.DoesNotContain("manifest.json", cfg.FilesToCopy);
    }

    [Fact]
    public async Task Apply_SerializesUiOptionsIntoConfig()
    {
        SetupV2Available();
        File.WriteAllText(Path.Combine(installDir, UpdaterFileName), "shim");
        using UpdateService svc = Build(updaterUi: new UpdaterUiOptions
        {
            WindowTitle = "Nullwake",
            AccentColor = (120, 200, 255),
            BackgroundColor = (10, 14, 20),
            InstallingText = "Installing Nullwake",
            FinishingText = "Finishing up...",
        });

        await svc.CheckForUpdateAsync();
        await svc.StartDownloadAsync();
        Assert.True(svc.ApplyUpdate());

        string applyJson = File.ReadAllText(Path.Combine(appDataDir, "apply-update.json"));
        ApplyUpdateConfig cfg = System.Text.Json.JsonSerializer.Deserialize<ApplyUpdateConfig>(applyJson)!;
        Assert.NotNull(cfg.Ui);
        Assert.Equal("Nullwake", cfg.Ui!.WindowTitle);
        Assert.Equal(120, cfg.Ui.Accent!.R);
        Assert.Equal(255, cfg.Ui.Accent.B);
        Assert.Equal(10, cfg.Ui.Background!.R);
        Assert.Equal("Installing Nullwake", cfg.Ui.InstallingText);
        Assert.Equal("Finishing up...", cfg.Ui.FinishingText);
    }

    [Fact]
    public async Task Apply_NoUiOptions_LeavesConfigUiNull()
    {
        SetupV2Available();
        File.WriteAllText(Path.Combine(installDir, UpdaterFileName), "shim");
        using UpdateService svc = Build();

        await svc.CheckForUpdateAsync();
        await svc.StartDownloadAsync();
        Assert.True(svc.ApplyUpdate());

        string applyJson = File.ReadAllText(Path.Combine(appDataDir, "apply-update.json"));
        ApplyUpdateConfig cfg = System.Text.Json.JsonSerializer.Deserialize<ApplyUpdateConfig>(applyJson)!;
        Assert.Null(cfg.Ui);
    }

    [Fact]
    public async Task Apply_MissingShim_Fails()
    {
        SetupV2Available();
        // No updater file created in installDir.
        using UpdateService svc = Build();

        await svc.CheckForUpdateAsync();
        await svc.StartDownloadAsync();
        bool started = svc.ApplyUpdate();

        Assert.False(started);
        Assert.Equal(UpdateState.Failed, svc.State);
        Assert.False(exited);
    }

    [Fact]
    public void Ctor_DetectsInterruptedApplyMarker()
    {
        File.WriteAllText(Path.Combine(appDataDir, "apply-in-progress.json"), "{}");

        using UpdateService svc = Build();

        Assert.True(svc.PreviousUpdateInterrupted);
        Assert.False(File.Exists(Path.Combine(appDataDir, "apply-in-progress.json"))); // cleared
    }

    // --- Signed-manifest hardening (Task 5) ---

    [Fact]
    public void Ctor_NoTrustedKeys_Throws()
    {
        Assert.Throws<ArgumentException>(() => Build(trustedKeys: Array.Empty<string>()));
    }

    [Fact]
    public async Task Check_UnsignedManifest_DoesNotOfferUpdate()
    {
        // Publish manifest bytes + Latest but NO ".sig" entry.
        var remote = new UpdateManifest { Version = "2.0.0", Platform = "win-x64" };
        remote.Files.Add(new ManifestFileEntry { Path = "game.dll", Sha256 = FakeUpdateSource.Sha("v2"), Size = 2 });
        source.Bytes[ManifestUrl] = Encoding.UTF8.GetBytes(remote.Serialize());
        source.Latest = new LatestVersionInfo("2.0.0", "2.0.0", ManifestUrl, Required: false);
        using UpdateService svc = Build();

        await svc.CheckForUpdateAsync();

        Assert.Equal(UpdateState.Idle, svc.State);
    }

    [Fact]
    public async Task Check_WrongKeySignature_DoesNotOfferUpdate()
    {
        using var otherKey = System.Security.Cryptography.RSA.Create(2048);
        var remote = new UpdateManifest { Version = "2.0.0", Platform = "win-x64" };
        remote.Files.Add(new ManifestFileEntry { Path = "game.dll", Sha256 = FakeUpdateSource.Sha("v2"), Size = 2 });
        // Sign with a DIFFERENT key than the trusted PubPem.
        source.PublishSigned(remote, ManifestUrl, otherKey.ExportRSAPrivateKeyPem());
        using UpdateService svc = Build();

        await svc.CheckForUpdateAsync();

        Assert.Equal(UpdateState.Idle, svc.State);
    }

    [Fact]
    public async Task Check_ValidSignature_OffersUpdate()
    {
        File.WriteAllText(Path.Combine(installDir, "game.dll"), "v1");
        string gameSha = source.Add("game.dll", "v2");
        var remote = new UpdateManifest { Version = "2.0.0", Platform = "win-x64" };
        remote.Files.Add(new ManifestFileEntry { Path = "game.dll", Sha256 = gameSha, Size = 2 });
        source.PublishSigned(remote, ManifestUrl, PrivPem);
        using UpdateService svc = Build();

        await svc.CheckForUpdateAsync();

        Assert.Equal(UpdateState.UpdateAvailable, svc.State);
        Assert.Equal("2.0.0", svc.RemoteVersion);
    }

    [Fact]
    public async Task Check_SignedRequiredFlag_IsUsed_NotTheLatestResponse()
    {
        // Signed manifest says Required=true; the Latest response says required=false. The signed
        // field must win.
        var remote = new UpdateManifest { Version = "2.0.0", Platform = "win-x64", Required = true };
        remote.Files.Add(new ManifestFileEntry { Path = "new.dll", Sha256 = source.Add("new.dll", "n1"), Size = 2 });
        byte[] manifestBytes = Encoding.UTF8.GetBytes(remote.Serialize());
        source.Bytes[ManifestUrl] = manifestBytes;
        source.Bytes[ManifestUrl + ".sig"] = ManifestSigner.Sign(manifestBytes, PrivPem);
        source.RemoteManifest = remote;
        source.Latest = new LatestVersionInfo("2.0.0", "2.0.0", ManifestUrl, Required: false);
        using UpdateService svc = Build();

        await svc.CheckForUpdateAsync();

        Assert.Equal(UpdateState.UpdateAvailable, svc.State);
        Assert.True(svc.IsRequired);
    }

    [Fact]
    public async Task Check_Downgrade_IsRejected_EvenIfSigned()
    {
        // Signed manifest version 1.0.0, current 2.0.0 -> reject.
        var remote = new UpdateManifest { Version = "1.0.0", Platform = "win-x64" };
        remote.Files.Add(new ManifestFileEntry { Path = "game.dll", Sha256 = FakeUpdateSource.Sha("v0"), Size = 2 });
        // Latest claims to be newer to get past the first gate; the signed downgrade check must catch it.
        byte[] manifestBytes = Encoding.UTF8.GetBytes(remote.Serialize());
        source.Bytes[ManifestUrl] = manifestBytes;
        source.Bytes[ManifestUrl + ".sig"] = ManifestSigner.Sign(manifestBytes, PrivPem);
        source.RemoteManifest = remote;
        source.Latest = new LatestVersionInfo("3.0.0", "3.0.0", ManifestUrl, Required: false);
        using UpdateService svc = Build(currentVersion: "2.0.0");

        await svc.CheckForUpdateAsync();

        Assert.Equal(UpdateState.Idle, svc.State);
    }

    [Fact]
    public async Task Check_RemoteVersion_ComesFromSignedManifest_NotLatestResponse()
    {
        // The unsigned Latest response advertises 2.5.0, but the SIGNED manifest says 2.0.0. The
        // signed version is authoritative for RemoteVersion (it feeds the recorded installed version).
        File.WriteAllText(Path.Combine(installDir, "game.dll"), "v1");
        string gameSha = source.Add("game.dll", "v2");
        var remote = new UpdateManifest { Version = "2.0.0", Platform = "win-x64" };
        remote.Files.Add(new ManifestFileEntry { Path = "game.dll", Sha256 = gameSha, Size = 2 });
        byte[] manifestBytes = Encoding.UTF8.GetBytes(remote.Serialize());
        source.Bytes[ManifestUrl] = manifestBytes;
        source.Bytes[ManifestUrl + ".sig"] = ManifestSigner.Sign(manifestBytes, PrivPem);
        source.RemoteManifest = remote;
        // Unsigned advertised version differs from (and is newer than) the signed one; both > current.
        source.Latest = new LatestVersionInfo("2.5.0", "2.5.0", ManifestUrl, Required: false);
        using UpdateService svc = Build();

        await svc.CheckForUpdateAsync();

        Assert.Equal(UpdateState.UpdateAvailable, svc.State);
        Assert.Equal("2.0.0", svc.RemoteVersion);
    }

    [Fact]
    public async Task Check_FileOverCap_DoesNotOfferUpdate()
    {
        var remote = new UpdateManifest { Version = "2.0.0", Platform = "win-x64" };
        remote.Files.Add(new ManifestFileEntry { Path = "huge.dll", Sha256 = FakeUpdateSource.Sha("x"), Size = 5_000_000_000 });
        source.PublishSigned(remote, ManifestUrl, PrivPem);
        using UpdateService svc = Build(maxFileBytes: 1_000_000);

        await svc.CheckForUpdateAsync();

        Assert.Equal(UpdateState.Idle, svc.State);
    }
}
