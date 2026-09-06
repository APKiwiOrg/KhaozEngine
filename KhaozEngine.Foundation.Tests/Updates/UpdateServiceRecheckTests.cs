using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using KhaozEngine.Updates;
using Xunit;

namespace KhaozEngine.Tests.Updates;

/// <summary>
/// Covers the opt-in in-session recheck (<see cref="UpdateServiceOptions.RecheckInterval"/> +
/// <see cref="UpdateService.Tick"/>): the Idle-only accumulator, the interval fire, the non-Idle
/// reset, and the single-flight guard. The fake source completes checks synchronously (gate null),
/// so a Tick-driven fire-and-forget check runs to completion inside the Tick call and every assertion
/// is deterministic. The in-flight test uses the gate to hold a check suspended.
/// </summary>
public sealed class UpdateServiceRecheckTests : IDisposable
{
    private readonly string root;
    private readonly string installDir;
    private readonly string appDataDir;
    private readonly FakeUpdateSource source = new();

    private static readonly System.Security.Cryptography.RSA SignKey = System.Security.Cryptography.RSA.Create(2048);
    private static string PrivPem => SignKey.ExportRSAPrivateKeyPem();
    private static string PubPem => SignKey.ExportSubjectPublicKeyInfoPem();

    private const string ManifestUrl = "https://u.example.com/2.0.0/manifest.json";

    public UpdateServiceRecheckTests()
    {
        root = Path.Combine(Path.GetTempPath(), "ke-updates-recheck-" + Guid.NewGuid().ToString("N"));
        installDir = Path.Combine(root, "install");
        appDataDir = Path.Combine(root, "appdata");
        Directory.CreateDirectory(installDir);
        Directory.CreateDirectory(appDataDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private UpdateService Build(TimeSpan? recheckInterval = null, string currentVersion = "1.0.0")
        => new(new UpdateServiceOptions
        {
            Source = source,
            CurrentVersion = currentVersion,
            TrustedPublicKeys = new[] { PubPem },
            InstallDir = installDir,
            AppDataDir = appDataDir,
            Platform = "win-x64",
            UpdaterExecutableName = "TestUpdater",
            DisposeSource = false,
            LaunchUpdater = (u, c) => true,
            ExitProcess = () => { },
            RecheckInterval = recheckInterval,
        });

    /// <summary>Publishes a signed v2.0.0 manifest changing game.dll and adding new.dll (mirrors UpdateServiceTests).</summary>
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

    /// <summary>Points the feed at an up-to-date answer so a subsequent check lands back at Idle.</summary>
    private void FeedUpToDate() => source.Latest = new LatestVersionInfo("1.0.0", "1.0.0", ManifestUrl, false);

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(10);
        }
        Assert.True(condition(), "condition not met within timeout");
    }

    [Fact]
    public void Tick_BeforeInterval_DoesNotCheck()
    {
        SetupV2Available();
        using UpdateService svc = Build(recheckInterval: TimeSpan.FromSeconds(10));

        svc.Tick(4f);
        svc.Tick(5f); // 9s total, under the 10s interval

        Assert.Equal(0, source.CheckCalls);
        Assert.Equal(UpdateState.Idle, svc.State);
    }

    [Fact]
    public void Tick_ReachesInterval_ChecksAndNoticesUpdate()
    {
        SetupV2Available();
        using UpdateService svc = Build(recheckInterval: TimeSpan.FromSeconds(10));

        svc.Tick(6f);
        Assert.Equal(0, source.CheckCalls); // 6 < 10, no fire yet

        svc.Tick(6f); // 12 >= 10 -> fire. The synchronous fake runs the whole check inline

        Assert.Equal(1, source.CheckCalls);
        Assert.Equal(UpdateState.UpdateAvailable, svc.State);
        Assert.Equal("2.0.0", svc.RemoteVersion);
    }

    [Fact]
    public async Task Tick_AfterUntrustedResponse_RechecksAfterFreshInterval()
    {
        SetupV2Available();
        byte[] signature = source.Bytes[ManifestUrl + ".sig"];
        source.Bytes.Remove(ManifestUrl + ".sig");
        using UpdateService svc = Build(recheckInterval: TimeSpan.FromSeconds(10));

        await svc.CheckForUpdateAsync();
        Assert.Equal(UpdateState.Untrusted, svc.State);

        source.Bytes[ManifestUrl + ".sig"] = signature;
        svc.Tick(9f);
        Assert.Equal(1, source.CheckCalls);
        svc.Tick(1f);

        Assert.Equal(2, source.CheckCalls);
        Assert.Equal(UpdateState.UpdateAvailable, svc.State);
        Assert.Null(svc.ErrorMessage);
    }

    [Fact]
    public async Task Tick_InNonIdleState_DoesNotAccumulateOrFire()
    {
        SetupV2Available();
        using UpdateService svc = Build(recheckInterval: TimeSpan.FromSeconds(10));

        await svc.CheckForUpdateAsync(); // -> UpdateAvailable (non-Idle)
        Assert.Equal(UpdateState.UpdateAvailable, svc.State);
        Assert.Equal(1, source.CheckCalls);

        // Many big frames while non-Idle: nothing accrues and nothing fires.
        for (int i = 0; i < 5; i++)
        {
            svc.Tick(1000f);
        }

        Assert.Equal(1, source.CheckCalls);
        Assert.Equal(UpdateState.UpdateAvailable, svc.State);
    }

    [Fact]
    public async Task Tick_AfterExcursion_RequiresFreshInterval()
    {
        SetupV2Available();
        using UpdateService svc = Build(recheckInterval: TimeSpan.FromSeconds(10));

        // Accrue under the interval, then a recheck fires and enters a non-Idle flow.
        svc.Tick(9f);
        Assert.Equal(0, source.CheckCalls);
        svc.Tick(2f); // 11 >= 10 -> fire, lands at UpdateAvailable
        Assert.Equal(1, source.CheckCalls);
        Assert.Equal(UpdateState.UpdateAvailable, svc.State);

        // Frames during the non-Idle flow must not accrue toward the next recheck.
        svc.Tick(100f);
        Assert.Equal(1, source.CheckCalls);

        // Flow ends: feed is now up to date, so a re-check lands back at Idle.
        FeedUpToDate();
        await svc.CheckForUpdateAsync();
        Assert.Equal(UpdateState.Idle, svc.State);
        Assert.Equal(2, source.CheckCalls);

        // A fresh FULL interval is required: a partial tick must not re-probe.
        svc.Tick(9f);
        Assert.Equal(2, source.CheckCalls);
        svc.Tick(1f); // 10 -> fire
        Assert.Equal(3, source.CheckCalls);
    }

    [Fact]
    public void Tick_NullInterval_NeverChecks()
    {
        SetupV2Available();
        using UpdateService svc = Build(recheckInterval: null);

        for (int i = 0; i < 10; i++)
        {
            svc.Tick(1000f);
        }

        Assert.Equal(0, source.CheckCalls);
        Assert.Equal(UpdateState.Idle, svc.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Tick_NonPositiveInterval_NeverChecks(int seconds)
    {
        SetupV2Available();
        using UpdateService svc = Build(recheckInterval: TimeSpan.FromSeconds(seconds));

        for (int i = 0; i < 5; i++)
        {
            svc.Tick(1000f);
        }

        Assert.Equal(0, source.CheckCalls);
        Assert.Equal(UpdateState.Idle, svc.State);
    }

    [Fact]
    public void Tick_NegativeOrNaNDelta_DoesNotAccumulate()
    {
        SetupV2Available();
        using UpdateService svc = Build(recheckInterval: TimeSpan.FromSeconds(10));

        // Bad deltas must contribute nothing: no negative drift, no NaN poisoning of the clock.
        for (int i = 0; i < 100; i++)
        {
            svc.Tick(-1f);
            svc.Tick(float.NaN);
        }
        Assert.Equal(0, source.CheckCalls);

        // 9 real seconds is still under the interval (proves the bad deltas were ignored, not summed).
        svc.Tick(9f);
        Assert.Equal(0, source.CheckCalls);
        // One more second reaches the interval and fires (proves the clock was not NaN/negative).
        svc.Tick(1f);
        Assert.Equal(1, source.CheckCalls);
    }

    [Fact]
    public void Tick_AfterDispose_NeverChecks()
    {
        SetupV2Available();
        UpdateService svc = Build(recheckInterval: TimeSpan.FromSeconds(1));
        svc.Dispose();

        for (int i = 0; i < 5; i++)
        {
            svc.Tick(1000f);
        }

        Assert.Equal(0, source.CheckCalls);
    }

    [Fact]
    public async Task Tick_WhileCheckInFlight_DoesNotDoubleFire()
    {
        SetupV2Available();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.CheckGate = gate;
        using UpdateService svc = Build(recheckInterval: TimeSpan.FromSeconds(1));

        svc.Tick(1f); // fires, the check parks on the gate (state stays Checking)
        Assert.Equal(1, source.CheckCalls);
        Assert.Equal(UpdateState.Checking, svc.State);

        // More than an interval's worth of ticks while the first check is still parked: no second check.
        svc.Tick(1f);
        svc.Tick(1f);
        Assert.Equal(1, source.CheckCalls);
        Assert.Equal(UpdateState.Checking, svc.State);

        // Release the in-flight check and let it settle to UpdateAvailable.
        gate.SetResult(true);
        await WaitUntil(() => svc.State == UpdateState.UpdateAvailable);
        Assert.Equal(1, source.CheckCalls);
    }

    [Fact]
    public void Tick_ThrowingStateChangedSubscriber_DoesNotEscapeOrWedge()
    {
        SetupV2Available();
        FeedUpToDate(); // the fired check lands back at Idle, proving the service is not wedged
        using UpdateService svc = Build(recheckInterval: TimeSpan.FromSeconds(10));
        // Throws on EVERY invocation, so the recovery transition back to Idle is exercised too.
        svc.StateChanged += () => throw new InvalidOperationException("consumer handler bug");

        Exception? escaped = Record.Exception(() => svc.Tick(10f));

        Assert.Null(escaped); // a broken subscriber must never escape the game-loop Tick
        Assert.Equal(1, source.CheckCalls); // the check itself still ran
        Assert.Equal(UpdateState.Idle, svc.State); // not wedged in Checking

        // A subsequent recheck still fires after a fresh full interval.
        svc.Tick(9f);
        Assert.Equal(1, source.CheckCalls);
        svc.Tick(1f);
        Assert.Equal(2, source.CheckCalls);
    }

    [Fact]
    public async Task CheckForUpdateAsync_ResetsRecheckAccumulator()
    {
        SetupV2Available();
        FeedUpToDate(); // manual check lands at Idle rather than entering a flow
        using UpdateService svc = Build(recheckInterval: TimeSpan.FromSeconds(10));

        svc.Tick(9f); // clock at 9, just under the interval
        Assert.Equal(0, source.CheckCalls);

        await svc.CheckForUpdateAsync(); // manual check must also zero the clock
        Assert.Equal(1, source.CheckCalls);
        Assert.Equal(UpdateState.Idle, svc.State);

        // The manual check reset the clock, so a 9s tick must NOT immediately fire a recheck.
        svc.Tick(9f);
        Assert.Equal(1, source.CheckCalls);
        // A fresh full interval does.
        svc.Tick(1f);
        Assert.Equal(2, source.CheckCalls);
    }
}
