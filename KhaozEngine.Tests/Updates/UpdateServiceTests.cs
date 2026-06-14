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

    private UpdateService Build(string currentVersion = "1.0.0", int maxRetries = 2)
        => new(new UpdateServiceOptions
        {
            Source = source,
            CurrentVersion = currentVersion,
            InstallDir = installDir,
            AppDataDir = appDataDir,
            Platform = "win-x64",
            UpdaterExecutableName = "TestUpdater",
            MaxDownloadRetries = maxRetries,
            DisposeSource = false,
            LaunchUpdater = (u, c) => { launches.Add((u, c)); return true; },
            ExitProcess = () => exited = true
        });

    private string StagingDir(string version) => Path.Combine(appDataDir, "update-staging", version);

    /// <summary>
    /// Sets up a standard "v2 available" scenario: install has game.dll@v1; remote changes game.dll
    /// and adds new.dll. Returns the remote manifest the fake source will serve.
    /// </summary>
    private void SetupV2Available()
    {
        File.WriteAllText(Path.Combine(installDir, "game.dll"), "v1");

        string gameSha = source.Add("game.dll", "v2");
        string newSha = source.Add("new.dll", "n1");

        var remote = new UpdateManifest { Version = "2.0.0", Platform = "win-x64" };
        remote.Files.Add(new ManifestFileEntry { Path = "game.dll", Sha256 = gameSha, Size = 2 });
        remote.Files.Add(new ManifestFileEntry { Path = "new.dll", Sha256 = newSha, Size = 2 });
        source.RemoteManifest = remote;
        source.Latest = new LatestVersionInfo("2.0.0", "2.0.0", "https://host/2.0.0/win-x64/manifest.json", Required: false);
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
}
