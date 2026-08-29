using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Diagnostics;
using KhaozEngine.Updates;
using Xunit;

namespace KhaozEngine.Tests.Updates;

/// <summary>
/// The per-session apply cap (#739): a repeatedly failing apply stops offering a retry, so a player whose
/// environment cannot install an update (a read-only install dir, an AV lock on the shim, a full disk) is not
/// walked round check -&gt; download -&gt; failed apply forever.
///
/// <para>In the LoggingSerial collection because one test configures the process-wide <see cref="Log"/> to
/// assert the once-per-session warning.</para>
/// </summary>
[Collection("LoggingSerial")]
public sealed class UpdateServiceApplyCapTests : IDisposable
{
    private readonly string root;
    private readonly string installDir;
    private readonly string appDataDir;
    private readonly FakeUpdateSource source = new();

    private static readonly System.Security.Cryptography.RSA SignKey = System.Security.Cryptography.RSA.Create(2048);
    private const string ManifestUrl = "https://u.example.com/2.0.0/manifest.json";

    public UpdateServiceApplyCapTests()
    {
        root = Path.Combine(Path.GetTempPath(), "ke-updates-cap-" + Guid.NewGuid().ToString("N"));
        installDir = Path.Combine(root, "install");
        appDataDir = Path.Combine(root, "appdata");
        Directory.CreateDirectory(installDir);
        Directory.CreateDirectory(appDataDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private UpdateService Build(int maxApplyAttempts = 2)
        => new(new UpdateServiceOptions
        {
            Source = source,
            CurrentVersion = "1.0.0",
            TrustedPublicKeys = new[] { SignKey.ExportSubjectPublicKeyInfoPem() },
            InstallDir = installDir,
            AppDataDir = appDataDir,
            Platform = "win-x64",
            // The install dir deliberately has NO shim, so every ApplyUpdate fails the same way a real
            // environmental failure does.
            UpdaterExecutableName = "TestUpdater",
            MaxApplyAttemptsPerSession = maxApplyAttempts,
            DisposeSource = false,
            LaunchUpdater = (_, _) => true,
            ExitProcess = () => { },
        });

    /// <summary>Install has game.dll@v1; the signed remote offers game.dll@v2 plus new.dll.</summary>
    private void SetupV2Available()
    {
        File.WriteAllText(Path.Combine(installDir, "game.dll"), "v1");
        var remote = new UpdateManifest { Version = "2.0.0", Platform = "win-x64" };
        remote.Files.Add(new ManifestFileEntry { Path = "game.dll", Sha256 = source.Add("game.dll", "v2"), Size = 2 });
        remote.Files.Add(new ManifestFileEntry { Path = "new.dll", Sha256 = source.Add("new.dll", "n1"), Size = 2 });
        source.PublishSigned(remote, ManifestUrl, SignKey.ExportRSAPrivateKeyPem());
    }

    /// <summary>One round of what the overlay's Failed prompt does: retry the check, then apply again. The
    /// files are already staged, so the check lands straight on ReadyToApply.</summary>
    private static async Task RetryAsync(UpdateService svc)
    {
        await svc.CheckForUpdateAsync();
        svc.ApplyUpdate();
    }

    [Fact]
    public async Task The_second_failed_apply_stops_offering_the_retry()
    {
        SetupV2Available();
        using UpdateService svc = Build();

        await svc.CheckForUpdateAsync();
        await svc.StartDownloadAsync();
        Assert.False(svc.ApplyUpdate());

        // One failure: the player still gets a retry, exactly as before.
        Assert.Equal(UpdateState.Failed, svc.State);
        Assert.Equal(1, svc.FailedApplyAttempts);
        Assert.False(svc.ApplyAttemptsExhausted);
        Assert.Equal(OverlayAction.Retry, UpdateOverlayActions.ResolveAction(svc));

        await RetryAsync(svc);

        // Two: the budget is spent and the overlay has nothing left to offer.
        Assert.Equal(UpdateState.Failed, svc.State);
        Assert.Equal(2, svc.FailedApplyAttempts);
        Assert.True(svc.ApplyAttemptsExhausted);
        Assert.Equal(OverlayAction.None, UpdateOverlayActions.ResolveAction(svc));
    }

    [Fact]
    public async Task A_trigger_past_the_cap_does_not_start_another_cycle()
    {
        SetupV2Available();
        using UpdateService svc = Build();

        await svc.CheckForUpdateAsync();
        await svc.StartDownloadAsync();
        svc.ApplyUpdate();
        await RetryAsync(svc);
        Assert.True(svc.ApplyAttemptsExhausted);

        int checksBefore = source.CheckCalls;
        UpdateOverlayActions.Trigger(svc);
        UpdateOverlayActions.Trigger(svc);

        Assert.Equal(checksBefore, source.CheckCalls); // no re-check, so no new download to fail on
        Assert.Equal(UpdateState.Failed, svc.State);
    }

    [Fact]
    public async Task A_failed_download_is_not_an_apply_attempt()
    {
        SetupV2Available();
        source.AlwaysCorrupt.Add("game.dll");
        using UpdateService svc = Build();

        await svc.CheckForUpdateAsync();
        await svc.StartDownloadAsync();

        Assert.Equal(UpdateState.Failed, svc.State);
        Assert.Equal(0, svc.FailedApplyAttempts);
        Assert.False(svc.ApplyAttemptsExhausted);
        Assert.Equal(OverlayAction.Retry, UpdateOverlayActions.ResolveAction(svc));
    }

    [Fact]
    public async Task A_non_positive_cap_keeps_offering_the_retry()
    {
        SetupV2Available();
        using UpdateService svc = Build(maxApplyAttempts: 0);

        await svc.CheckForUpdateAsync();
        await svc.StartDownloadAsync();
        svc.ApplyUpdate();
        await RetryAsync(svc);
        await RetryAsync(svc);

        Assert.Equal(3, svc.FailedApplyAttempts);
        Assert.False(svc.ApplyAttemptsExhausted);
        Assert.Equal(OverlayAction.Retry, UpdateOverlayActions.ResolveAction(svc));
    }

    [Fact]
    public async Task A_fresh_service_starts_the_session_with_a_full_budget()
    {
        SetupV2Available();
        using (UpdateService first = Build())
        {
            await first.CheckForUpdateAsync();
            await first.StartDownloadAsync();
            first.ApplyUpdate();
            await RetryAsync(first);
            Assert.True(first.ApplyAttemptsExhausted);
        }

        // A new session is a new object: the count is per-service, so relaunching clears it.
        using UpdateService second = Build();
        Assert.Equal(0, second.FailedApplyAttempts);
        Assert.False(second.ApplyAttemptsExhausted);
    }

    [Fact]
    public async Task Spending_the_budget_warns_exactly_once()
    {
        SetupV2Available();
        var sink = new InMemorySink();
        var options = new LoggerOptions { Synchronous = true, MinimumLevel = LogLevel.Trace, DefaultCategory = "App" };
        options.Sinks.Add(sink);

        try
        {
            Log.Configure(options);
            using UpdateService svc = Build();

            await svc.CheckForUpdateAsync();
            await svc.StartDownloadAsync();
            svc.ApplyUpdate();      // failure 1: below the cap, no warning
            await RetryAsync(svc);  // failure 2: spends the budget, warns
            await RetryAsync(svc);  // failure 3: already spent, must not warn again

            List<LogEntry> warnings = sink.Entries
                .Where(e => e.Level == LogLevel.Warn && e.Message.Contains("No further retry will be offered"))
                .ToList();
            Assert.Single(warnings);
            Assert.Contains("2 time(s)", warnings[0].Message);
        }
        finally { Log.Shutdown(); }
    }
}
