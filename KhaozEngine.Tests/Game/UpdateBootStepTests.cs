using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using KhaozEngine.Game;
using KhaozEngine.Tests.Updates;
using KhaozEngine.Updates;
using Xunit;

namespace KhaozEngine.Tests.Game;

/// <summary>
/// Covers <see cref="UpdateBootStep"/>'s mapping of the composed update gate onto boot-step results, over a fake
/// feed with the launch / exit hooks stubbed (no real network or relaunch). Mirrors the setup in
/// <c>UpdateGateTests</c>. Driving the pipeline pumps until it settles, since the gate does its work on pool threads.
/// </summary>
public sealed class UpdateBootStepTests : IDisposable
{
    readonly string _root;
    readonly string _installDir;
    readonly string _appDataDir;
    readonly FakeUpdateSource _source = new();
    bool _exited;

    static readonly System.Security.Cryptography.RSA SignKey = System.Security.Cryptography.RSA.Create(2048);
    static string PrivPem => SignKey.ExportRSAPrivateKeyPem();
    static string PubPem => SignKey.ExportSubjectPublicKeyInfoPem();
    const string ManifestUrl = "https://u.example.com/2.0.0/manifest.json";

    public UpdateBootStepTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ke-boot-update-" + Guid.NewGuid().ToString("N"));
        _installDir = Path.Combine(_root, "install");
        _appDataDir = Path.Combine(_root, "appdata");
        Directory.CreateDirectory(_installDir);
        Directory.CreateDirectory(_appDataDir);
        string updaterName = OperatingSystem.IsWindows() ? "TestUpdater.exe" : "TestUpdater";
        File.WriteAllText(Path.Combine(_installDir, updaterName), "shim");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    UpdateService Build() => new(new UpdateServiceOptions
    {
        Source = _source,
        CurrentVersion = "1.0.0",
        TrustedPublicKeys = new[] { PubPem },
        InstallDir = _installDir,
        AppDataDir = _appDataDir,
        Platform = "win-x64",
        UpdaterExecutableName = "TestUpdater",
        DisposeSource = false,
        LaunchUpdater = (u, c) => true,
        ExitProcess = () => _exited = true,
    });

    void SetupV2Available()
    {
        File.WriteAllText(Path.Combine(_installDir, "game.dll"), "v1");
        string gameSha = _source.Add("game.dll", "v2");
        var remote = new UpdateManifest { Version = "2.0.0", Platform = "win-x64" };
        remote.Files.Add(new ManifestFileEntry { Path = "game.dll", Sha256 = gameSha, Size = 2 });
        _source.PublishSigned(remote, ManifestUrl, PrivPem);
    }

    static BootState DriveToTerminal(BootPipeline pipeline, int maxPumps = 400)
    {
        pipeline.Start();
        for (int i = 0; i < maxPumps && !IsTerminal(pipeline.State); i++)
        {
            pipeline.Pump();
            if (IsTerminal(pipeline.State)) break;
            Thread.Sleep(2);
        }
        return pipeline.State;
    }

    static bool IsTerminal(BootState s) =>
        s is BootState.Completed or BootState.Failed or BootState.Restarting or BootState.Cancelled;

    [Fact]
    public void UpToDate_Feed_Proceeds()
    {
        _source.Latest = new LatestVersionInfo("1.0.0", "1.0.0", ManifestUrl, false);
        using UpdateService svc = Build();
        var pipeline = new BootPipeline(new IBootStep[] { new UpdateBootStep(svc) });

        BootState state = DriveToTerminal(pipeline);

        Assert.Equal(BootState.Completed, state);
        Assert.False(_exited);
    }

    [Fact]
    public void FeedUnreachable_Proceeds()
    {
        // Latest stays null -> the gate returns FeedUnreachable -> the step proceeds on the current build.
        using UpdateService svc = Build();
        var pipeline = new BootPipeline(new IBootStep[] { new UpdateBootStep(svc) });

        BootState state = DriveToTerminal(pipeline);

        Assert.Equal(BootState.Completed, state);
    }

    [Fact]
    public void NewerBuild_TriggersRestart()
    {
        SetupV2Available();
        using UpdateService svc = Build();
        var pipeline = new BootPipeline(new IBootStep[] { new UpdateBootStep(svc) });

        BootState state = DriveToTerminal(pipeline);

        Assert.Equal(BootState.Restarting, state);
        Assert.True(_exited); // ApplyUpdate ran the (stubbed) exit hook, i.e. the process would relaunch in production
    }
}
