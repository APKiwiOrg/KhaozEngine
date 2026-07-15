using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Updates;
using Xunit;

namespace KhaozEngine.Tests.Updates;

public sealed class UpdateApplierTests
{
    private const string Install = "/install";
    private const string Staging = "/staging";
    private const string AppData = "/appdata";

    private static string InstallPath(string rel) => Path.Combine(Install, rel.Replace('/', Path.DirectorySeparatorChar));
    private static string StagingPath(string rel) => Path.Combine(Staging, rel.Replace('/', Path.DirectorySeparatorChar));

    private static ApplyUpdateConfig Config(List<string> copy, List<string>? delete = null)
        => new()
        {
            TargetVersion = "2.0.0",
            InstallDir = Install,
            StagingDir = Staging,
            FilesToCopy = copy,
            FilesToDelete = delete ?? new List<string>(),
            GameExePath = InstallPath("Game"),
            ParentPid = 1234,
            ManifestDestPath = Path.Combine(AppData, "update-manifest.json")
        };

    [Fact]
    public void Apply_CopiesStagedFilesIntoInstall()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[StagingPath("data/x.bin")] = "bin2";
        env.Files[InstallPath("game.dll")] = "v1";
        env.Files[InstallPath("Game")] = "exe";

        ApplyResult result = UpdateApplier.Apply(Config(new() { "game.dll", "data/x.bin" }), env);

        Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("v2", env.Files[InstallPath("game.dll")]);
        Assert.Equal("bin2", env.Files[InstallPath("data/x.bin")]);
        Assert.Equal(InstallPath("Game"), env.RelaunchedExe);
        Assert.Equal(1, env.ParentWaits);
    }

    [Fact]
    public void Apply_DeletesRemovedFiles()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[InstallPath("game.dll")] = "v1";
        env.Files[InstallPath("old.dll")] = "stale";
        env.Files[InstallPath("Game")] = "exe";

        ApplyResult result = UpdateApplier.Apply(
            Config(new() { "game.dll" }, new List<string> { "old.dll" }), env);

        Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
        Assert.False(env.Files.ContainsKey(InstallPath("old.dll")));
    }

    [Fact]
    public void Apply_MissingStagedSource_AbortsBeforeTouchingInstall()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        // missing.dll deliberately absent from staging
        env.Files[InstallPath("game.dll")] = "v1";
        env.Files[InstallPath("Game")] = "exe";

        ApplyResult result = UpdateApplier.Apply(Config(new() { "game.dll", "missing.dll" }), env);

        Assert.Equal(ApplyOutcome.AbortedStagingIncomplete, result.Outcome);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("v1", env.Files[InstallPath("game.dll")]); // untouched
        Assert.Equal(InstallPath("Game"), env.RelaunchedExe);   // relaunched old version
    }

    [Fact]
    public void Apply_StagingIncomplete_LeavesNoMarker()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        // missing.dll deliberately absent from staging
        env.Files[InstallPath("Game")] = "exe";
        string marker = Path.Combine(AppData, "apply-in-progress.json");

        UpdateApplier.Apply(Config(new() { "game.dll", "missing.dll" }), env);

        Assert.False(env.Files.ContainsKey(marker)); // never written: aborted before the mutation phase
    }

    [Fact]
    public void Apply_CopyFails_RollsBackOverwrittenFiles()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("a.dll")] = "a-new";
        env.Files[StagingPath("b.dll")] = "b-new";
        env.Files[InstallPath("a.dll")] = "a-old";
        env.Files[InstallPath("b.dll")] = "b-old";
        env.Files[InstallPath("Game")] = "exe";
        env.ThrowOnCopyFrom.Add(StagingPath("b.dll")); // b never copies

        ApplyResult result = UpdateApplier.Apply(Config(new() { "a.dll", "b.dll" }), env);

        Assert.Equal(ApplyOutcome.RolledBack, result.Outcome);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("a-old", env.Files[InstallPath("a.dll")]); // restored
        Assert.Equal("b-old", env.Files[InstallPath("b.dll")]); // never changed
        Assert.Equal(InstallPath("Game"), env.RelaunchedExe);
        Assert.True(env.SleepCalls >= UpdateApplier.MaxCopyRetries - 1); // retried the locked file
    }

    [Fact]
    public void Apply_TransientPermissionDenial_RetriesThenSucceeds()
    {
        var env = new FakeUpdaterEnvironment { UnauthorizedReplaceThrows = 3 }; // denied 3x, then succeeds
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[InstallPath("game.dll")] = "v1";
        env.Files[InstallPath("Game")] = "exe";

        ApplyResult result = UpdateApplier.Apply(Config(new() { "game.dll" }), env);

        Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
        Assert.Equal("v2", env.Files[InstallPath("game.dll")]);
    }

    [Fact]
    public void Apply_PermanentPermissionDenial_RollsBackWithoutUnhandledThrow()
    {
        var env = new FakeUpdaterEnvironment { UnauthorizedReplaceThrows = UpdateApplier.MaxCopyRetries + 1 };
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[InstallPath("game.dll")] = "v1";
        env.Files[InstallPath("Game")] = "exe";
        string marker = Path.Combine(AppData, "apply-in-progress.json");

        ApplyResult result = UpdateApplier.Apply(Config(new() { "game.dll" }), env);

        Assert.Equal(ApplyOutcome.RolledBack, result.Outcome);
        Assert.Equal("v1", env.Files[InstallPath("game.dll")]); // restored
        Assert.False(env.Files.ContainsKey(marker));            // marker cleared
        Assert.Equal(InstallPath("Game"), env.RelaunchedExe);   // old version relaunched
    }

    [Fact]
    public void Apply_UnexpectedException_RollsBackAndClearsMarkerWithoutCrashing()
    {
        var env = new FakeUpdaterEnvironment { ThrowUnexpectedOnReplace = true };
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[InstallPath("game.dll")] = "v1";
        env.Files[InstallPath("Game")] = "exe";
        string marker = Path.Combine(AppData, "apply-in-progress.json");

        ApplyResult result = UpdateApplier.Apply(Config(new() { "game.dll" }), env); // must not throw

        Assert.Equal(ApplyOutcome.RolledBack, result.Outcome);
        Assert.Equal("v1", env.Files[InstallPath("game.dll")]); // restored
        Assert.False(env.Files.ContainsKey(marker));            // marker cleared
        Assert.Equal(InstallPath("Game"), env.RelaunchedExe);   // old version relaunched
    }

    [Fact]
    public void Apply_PostCommitFinishFailure_ReportsSuccessNotRollback()
    {
        var env = new FakeUpdaterEnvironment { ThrowOnSettleCheck = true };
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[InstallPath("game.dll")] = "v1";
        env.Files[InstallPath("Game")] = "exe";
        string marker = Path.Combine(AppData, "apply-in-progress.json");

        ApplyResult result = UpdateApplier.Apply(Config(new() { "game.dll" }), env); // must not throw

        Assert.Equal(ApplyOutcome.Succeeded, result.Outcome); // committed: not a rollback
        Assert.Equal("v2", env.Files[InstallPath("game.dll")]); // new file stays installed
        Assert.False(env.Files.ContainsKey(marker)); // marker cleared
    }

    [Fact]
    public void Apply_InstallsManifestToDestPath()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[StagingPath("manifest.json")] = "{\"version\":\"2.0.0\"}";
        env.Files[InstallPath("Game")] = "exe";

        UpdateApplier.Apply(Config(new() { "game.dll" }), env);

        Assert.Equal("{\"version\":\"2.0.0\"}", env.Files[Path.Combine(AppData, "update-manifest.json")]);
    }

    [Fact]
    public void Apply_ClearsProgressMarkerOnSuccess()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[InstallPath("Game")] = "exe";
        string marker = Path.Combine(AppData, "apply-in-progress.json");

        UpdateApplier.Apply(Config(new() { "game.dll" }), env);

        Assert.False(env.Files.ContainsKey(marker)); // written during apply, cleared at the end
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("../../escape.dll")]
    [InlineData("sub/../../escape.dll")]
    public void Apply_UnsafeCopyPath_AbortsBeforeTouchingInstall(string badPath)
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[InstallPath("game.dll")] = "v1";
        env.Files[InstallPath("Game")] = "exe";

        ApplyResult result = UpdateApplier.Apply(Config(new() { "game.dll", badPath }), env);

        Assert.Equal(ApplyOutcome.AbortedUnsafePath, result.Outcome);
        Assert.Equal("v1", env.Files[InstallPath("game.dll")]); // untouched
        Assert.Equal(InstallPath("Game"), env.RelaunchedExe);   // old version relaunched
    }

    [Fact]
    public void Apply_UnsafeDeletePath_AbortsBeforeTouchingInstall()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[InstallPath("game.dll")] = "v1";
        env.Files[InstallPath("Game")] = "exe";

        ApplyResult result = UpdateApplier.Apply(
            Config(new() { "game.dll" }, new List<string> { "../../secret" }), env);

        Assert.Equal(ApplyOutcome.AbortedUnsafePath, result.Outcome);
        Assert.Equal("v1", env.Files[InstallPath("game.dll")]);
    }

    [Fact]
    public void Apply_StagedSourceIsReparsePoint_Aborts()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        env.ReparsePoints.Add(StagingPath("game.dll"));
        env.Files[InstallPath("Game")] = "exe";

        ApplyResult result = UpdateApplier.Apply(Config(new() { "game.dll" }), env);

        Assert.Equal(ApplyOutcome.AbortedUnsafePath, result.Outcome);
    }

    [Fact]
    public void Apply_DestIsReparsePoint_RemovesLinkThenCopies()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[InstallPath("game.dll")] = "link";          // pretend this is a symlink
        env.ReparsePoints.Add(InstallPath("game.dll"));
        env.Files[InstallPath("Game")] = "exe";

        ApplyResult result = UpdateApplier.Apply(Config(new() { "game.dll" }), env);

        Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
        Assert.Equal("v2", env.Files[InstallPath("game.dll")]); // real file replaced the link
        Assert.Contains(env.Log_, m => m.Contains("removing link before copy")); // exercised the removal branch
    }

    [Fact]
    public void Apply_DestReparsePoint_LinkRemovalFails_AbortsWithoutCopyingThroughLink()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[InstallPath("game.dll")] = "link";
        env.ReparsePoints.Add(InstallPath("game.dll"));
        env.ThrowOnDeleteOf.Add(InstallPath("game.dll"));
        env.Files[InstallPath("Game")] = "exe";

        ApplyResult result = UpdateApplier.Apply(Config(new() { "game.dll" }), env);

        Assert.Equal(ApplyOutcome.AbortedUnsafePath, result.Outcome);
        Assert.Equal("link", env.Files[InstallPath("game.dll")]); // NOT overwritten through the link
        Assert.Equal(InstallPath("Game"), env.RelaunchedExe);
    }

    [Fact]
    public void Apply_DestReparsePoint_ThenLaterCopyFails_RollsBackOtherFiles()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("a.dll")] = "a-new";
        env.Files[StagingPath("b.dll")] = "b-new";
        env.Files[InstallPath("a.dll")] = "a-link";
        env.ReparsePoints.Add(InstallPath("a.dll"));   // a is a symlink dest, removed then copied
        env.Files[InstallPath("b.dll")] = "b-old";
        env.Files[InstallPath("Game")] = "exe";
        env.ThrowOnCopyFrom.Add(StagingPath("b.dll")); // b copy fails -> rollback

        ApplyResult result = UpdateApplier.Apply(Config(new() { "a.dll", "b.dll" }), env);

        Assert.Equal(ApplyOutcome.RolledBack, result.Outcome);
        Assert.Equal("b-old", env.Files[InstallPath("b.dll")]); // restored
        // a.dll's original link is intentionally NOT restored (documented contract); just assert no crash + outcome.
    }

    [Fact]
    public void Apply_CodeSignatureInvalid_RollsBackAndRelaunchesOld()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[InstallPath("game.dll")] = "v1";
        env.Files[InstallPath("Game")] = "exe";
        env.CodeSignatureValid = false;

        ApplyResult result = UpdateApplier.Apply(Config(new() { "game.dll" }), env);

        Assert.Equal(ApplyOutcome.RolledBack, result.Outcome);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("v1", env.Files[InstallPath("game.dll")]); // restored from backup
        Assert.Equal(InstallPath("Game"), env.RelaunchedExe);
    }

    [Fact]
    public void Apply_CodeSignatureInvalid_DoesNotInstallNewManifest()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[StagingPath("manifest.json")] = "{\"version\":\"2.0.0\"}";
        env.Files[InstallPath("game.dll")] = "v1";
        env.Files[InstallPath("Game")] = "exe";
        env.CodeSignatureValid = false;

        ApplyResult result = UpdateApplier.Apply(Config(new() { "game.dll" }), env);

        Assert.Equal(ApplyOutcome.RolledBack, result.Outcome);
        // Manifest dest must NOT have been written on a codesign-fail rollback.
        Assert.False(env.Files.ContainsKey(Path.Combine(AppData, "update-manifest.json")));
    }

    [Fact]
    public void Apply_CodeSignatureValid_Succeeds()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[InstallPath("game.dll")] = "v1";
        env.Files[InstallPath("Game")] = "exe";
        env.CodeSignatureValid = true;

        ApplyResult result = UpdateApplier.Apply(Config(new() { "game.dll" }), env);

        Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
        Assert.Equal("v2", env.Files[InstallPath("game.dll")]);
    }

    [Fact]
    public void Apply_ResealRunsBeforeVerify()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[InstallPath("game.dll")] = "v1";
        env.Files[InstallPath("Game")] = "exe";

        ApplyResult result = UpdateApplier.Apply(Config(new() { "game.dll" }), env);

        Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
        // The bundle must be re-sealed once, and the verify must observe that re-seal (an in-place swap
        // invalidates the seal, so verifying before re-sealing would always fail on macOS).
        Assert.Equal(1, env.ResealCalls);
        Assert.Equal(1, env.VerifyCalls);
        Assert.Equal(1, env.VerifyCalledAfterReseals); // verify saw the re-seal already done
    }

    [Fact]
    public void Apply_ResealFails_RollsBackAndRelaunchesOld()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[StagingPath("manifest.json")] = "{\"version\":\"2.0.0\"}";
        env.Files[InstallPath("game.dll")] = "v1";
        env.Files[InstallPath("Game")] = "exe";
        env.ResealSucceeds = false;

        ApplyResult result = UpdateApplier.Apply(Config(new() { "game.dll" }), env);

        Assert.Equal(ApplyOutcome.RolledBack, result.Outcome);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("v1", env.Files[InstallPath("game.dll")]);      // restored from backup
        Assert.Equal(InstallPath("Game"), env.RelaunchedExe);        // old version relaunched
        Assert.Equal(0, env.VerifyCalls);                            // failed re-seal short-circuits the verify
        Assert.False(env.Files.ContainsKey(Path.Combine(AppData, "update-manifest.json"))); // manifest not committed
    }

    [Fact]
    public void Apply_SettleWait_RelaunchesOnlyAfterExeBecomesOpenable()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[InstallPath("Game")] = "exe";
        // Model an AV scan holding the freshly-written exe: the first 3 settle polls report it locked,
        // then it becomes openable on the 4th.
        env.OpenExclusiveFailCount = 3;

        ApplyResult result = UpdateApplier.Apply(Config(new() { "game.dll" }), env);

        Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
        Assert.Equal(InstallPath("Game"), env.RelaunchedExe);
        // Polled 4 times total (3 locked + 1 openable), slept once per locked poll, and only relaunched
        // once the exe was openable (relaunch fired at the 4th CanOpenExclusively call, not before).
        Assert.Equal(4, env.CanOpenExclusivelyCalls);
        Assert.Equal(3, env.SleepCalls);
        Assert.Equal(4, env.OpenCallsAtRelaunch);
    }

    [Fact]
    public void Apply_SettleWait_TimesOutButStillRelaunches()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[InstallPath("Game")] = "exe";
        // Never becomes openable within the poll budget.
        env.OpenExclusiveFailCount = int.MaxValue;

        ApplyResult result = UpdateApplier.Apply(Config(new() { "game.dll" }), env);

        Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
        // Exhausted the whole poll budget, then relaunched anyway as a last resort.
        Assert.Equal(UpdateApplier.SettleMaxPolls, env.CanOpenExclusivelyCalls);
        Assert.Equal(InstallPath("Game"), env.RelaunchedExe);
        Assert.Contains(env.Log_, m => m.Contains("Timed out"));
    }

    // ---- Relaunch resilience: retry the Windows AV/image startup race (IUpdaterEnvironment.TryRelaunch) ----

    [Fact]
    public void Apply_RelaunchStartupFails_RetriesUntilItRuns()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[InstallPath("Game")] = "exe";
        // First two launches hit the AV/image race (fast startup failure), the third boots cleanly.
        env.RelaunchOutcomes.Enqueue(RelaunchStartupOutcome.StartupFailed);
        env.RelaunchOutcomes.Enqueue(RelaunchStartupOutcome.StartupFailed);
        env.RelaunchOutcomes.Enqueue(RelaunchStartupOutcome.Running);

        ApplyResult result = UpdateApplier.Apply(Config(new() { "game.dll" }), env);

        Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
        Assert.Equal(3, env.RelaunchAttempts);                // retried past the two failures
        Assert.Equal(InstallPath("Game"), env.RelaunchedExe); // the launch that stuck
        Assert.Equal(2, env.SleepCalls);                      // one back-off wait per failed attempt
        Assert.Contains(env.Log_, m => m.Contains("Relaunch attempt 1"));
        Assert.Contains(env.Log_, m => m.Contains("succeeded on attempt 3"));
    }

    [Fact]
    public void Apply_RelaunchNeverRuns_GivesUpAfterBudget_WithoutClaimingSuccess()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[InstallPath("Game")] = "exe";
        // Every attempt hits the startup race; the queue outlasts the attempt budget so it never runs.
        for (int i = 0; i < UpdateApplier.RelaunchMaxAttempts + 2; i++)
        {
            env.RelaunchOutcomes.Enqueue(RelaunchStartupOutcome.StartupFailed);
        }

        ApplyResult result = UpdateApplier.Apply(Config(new() { "game.dll" }), env);

        // The update itself applied (files are in place); only the auto-relaunch failed, so it is logged,
        // not turned into a rollback - the next manual/auto launch picks up the new version.
        Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
        Assert.Equal(UpdateApplier.RelaunchMaxAttempts, env.RelaunchAttempts); // bounded; no infinite loop
        Assert.Null(env.RelaunchedExe);                                        // never falsely reported a launch
        Assert.Contains(env.Log_, m => m.Contains("giving up"));
    }

    [Fact]
    public void Apply_RelaunchedGameExitsEarly_DoesNotRetry()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[InstallPath("Game")] = "exe";
        // The relaunched game ran and closed on its own (not a startup failure); the relaunch is done.
        env.RelaunchOutcomes.Enqueue(RelaunchStartupOutcome.ExitedEarly);

        UpdateApplier.Apply(Config(new() { "game.dll" }), env);

        Assert.Equal(1, env.RelaunchAttempts);                // no retry after a genuine run
        Assert.Equal(InstallPath("Game"), env.RelaunchedExe);
    }

    [Fact]
    public void Apply_RelaunchLaunchError_IsRetried()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[InstallPath("Game")] = "exe";
        env.RelaunchOutcomes.Enqueue(RelaunchStartupOutcome.LaunchError);
        env.RelaunchOutcomes.Enqueue(RelaunchStartupOutcome.Running);

        UpdateApplier.Apply(Config(new() { "game.dll" }), env);

        Assert.Equal(2, env.RelaunchAttempts);
        Assert.Equal(InstallPath("Game"), env.RelaunchedExe);
    }

    [Fact]
    public void Apply_InstallFiles_AreWrittenThroughTheAtomicReplacePath()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[StagingPath("Game")] = "exe-v2";
        env.Files[InstallPath("game.dll")] = "v1";
        env.Files[InstallPath("Game")] = "exe-v1";

        ApplyResult result = UpdateApplier.Apply(Config(new() { "game.dll", "Game" }), env);

        Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
        // Every install file - the exe included - is swapped in atomically (copy-to-temp + rename), never
        // a plain in-place overwrite that could leave a half-written image for the relaunch to hit.
        Assert.Contains(InstallPath("game.dll"), env.ReplacedDests);
        Assert.Contains(InstallPath("Game"), env.ReplacedDests);
        Assert.Equal("exe-v2", env.Files[InstallPath("Game")]);
    }

    [Fact]
    public void Apply_KeepsProgressWindowOpenAcrossRelaunchRetries_ThenClosesOnce()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[InstallPath("Game")] = "exe";
        env.RelaunchOutcomes.Enqueue(RelaunchStartupOutcome.StartupFailed);
        env.RelaunchOutcomes.Enqueue(RelaunchStartupOutcome.StartupFailed);
        env.RelaunchOutcomes.Enqueue(RelaunchStartupOutcome.Running);
        var ui = new RecordingUpdaterUi();
        int attemptsAtClose = -1;
        ui.OnClose = () => attemptsAtClose = env.RelaunchAttempts;

        UpdateApplier.Apply(Config(new() { "game.dll" }), env, ui);

        Assert.Equal(1, ui.CloseCalls);                       // closed exactly once
        Assert.Equal(3, attemptsAtClose);                     // ...and only after all three relaunch attempts ran
        Assert.Equal(UpdaterPhase.Finishing, ui.Phases[^1]);  // stayed in the Finishing (marquee) phase
    }

    [Fact]
    public void Run_BadArgs_ReturnsError()
    {
        Assert.Equal(1, UpdateApplier.Run(Array.Empty<string>(), new FakeUpdaterEnvironment()));
        Assert.Equal(1, UpdateApplier.Run(new[] { "--nope" }, new FakeUpdaterEnvironment()));
    }

    [Fact]
    public void Run_AppliesConfigFromDisk()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ke-updates-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string configPath = Path.Combine(dir, "apply-update.json");

        try
        {
            var env = new FakeUpdaterEnvironment();
            env.Files[StagingPath("game.dll")] = "v2";
            env.Files[InstallPath("Game")] = "exe";

            ApplyUpdateConfig config = Config(new() { "game.dll" });
            File.WriteAllText(configPath, System.Text.Json.JsonSerializer.Serialize(config));

            int exit = UpdateApplier.Run(new[] { "--apply", configPath }, env);

            Assert.Equal(0, exit);
            Assert.Equal("v2", env.Files[InstallPath("game.dll")]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Apply_GameStillRunningPastBarrier_AbortsUntouchedWithoutMarkerOrRelaunch()
    {
        var env = new FakeUpdaterEnvironment { ParentAlivePolls = 1000 }; // never exits within the budget
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[InstallPath("game.dll")] = "v1";
        env.Files[InstallPath("Game")] = "exe";
        string marker = Path.Combine(AppData, "apply-in-progress.json");

        ApplyResult result = UpdateApplier.Apply(Config(new() { "game.dll" }), env);

        Assert.Equal(ApplyOutcome.AbortedGameStillRunning, result.Outcome);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("v1", env.Files[InstallPath("game.dll")]); // untouched
        Assert.False(env.Files.ContainsKey(marker));            // no dangling marker
        Assert.Null(env.RelaunchedExe);                         // game alive: not relaunched
    }

    [Fact]
    public void Apply_ParentExitsAfterSomePolls_ProceedsAndApplies()
    {
        var env = new FakeUpdaterEnvironment { ParentAlivePolls = 3 }; // alive for 3 polls, then gone
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[InstallPath("game.dll")] = "v1";
        env.Files[InstallPath("Game")] = "exe";

        ApplyResult result = UpdateApplier.Apply(Config(new() { "game.dll" }), env);

        Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
        Assert.Equal("v2", env.Files[InstallPath("game.dll")]);
        Assert.Equal(InstallPath("Game"), env.RelaunchedExe);
    }

    // ---- Pre-mutation exclusive-open gate: a SIBLING instance the parent-pid barrier never watches ----

    [Fact]
    public void Apply_SiblingHoldsExeOpen_DefersUntouchedWithoutMarkerRollbackDirOrRelaunch()
    {
        // The launching instance exited cleanly (the parent-pid barrier above passes fine - ParentAlivePolls
        // defaults to 0), but the exe never becomes exclusively openable: a second live instance holds it.
        var env = new FakeUpdaterEnvironment { GateOpenExclusiveFailCount = int.MaxValue };
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[InstallPath("game.dll")] = "v1";
        env.Files[InstallPath("Game")] = "exe";
        string marker = Path.Combine(AppData, "apply-in-progress.json");

        ApplyResult result = UpdateApplier.Apply(Config(new() { "game.dll" }), env);

        Assert.Equal(ApplyOutcome.AbortedGameStillRunning, result.Outcome);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("v1", env.Files[InstallPath("game.dll")]); // install untouched
        Assert.False(env.Files.ContainsKey(marker));            // no marker ever written
        Assert.Empty(env.ReplacedDests);                        // no file mutation attempted at all
        Assert.Null(env.RelaunchedExe);                         // no relaunch - the running sibling is left alone
        Assert.Equal(UpdateApplier.ExeExclusiveGateMaxPolls, env.GateCanOpenExclusivelyCalls);
        Assert.Contains(env.Log_, m => m.Contains("still held open by another process"));
    }

    [Fact]
    public void Apply_ExeFreesUpDuringGateWait_AppliesNormally()
    {
        // A sibling (or an AV scan) holds the exe for the first few gate polls, then releases it - the
        // update should proceed exactly as if nothing had happened.
        var env = new FakeUpdaterEnvironment { GateOpenExclusiveFailCount = 3 };
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[InstallPath("game.dll")] = "v1";
        env.Files[InstallPath("Game")] = "exe";

        ApplyResult result = UpdateApplier.Apply(Config(new() { "game.dll" }), env);

        Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
        Assert.Equal("v2", env.Files[InstallPath("game.dll")]);
        Assert.Equal(InstallPath("Game"), env.RelaunchedExe);
        Assert.Equal(4, env.GateCanOpenExclusivelyCalls); // 3 locked + 1 openable
        Assert.Contains(env.Log_, m => m.Contains("became exclusively openable"));
    }

    [Fact]
    public void Apply_ExeFreeImmediately_GateAddsNoExtraDelay()
    {
        // The common path (env.GateOpenExclusiveFailCount defaults to 0): the gate must not add any sleep
        // or observable slowdown to an apply that would otherwise succeed on the first try.
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[InstallPath("game.dll")] = "v1";
        env.Files[InstallPath("Game")] = "exe";

        ApplyResult result = UpdateApplier.Apply(Config(new() { "game.dll" }), env);

        Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, env.GateCanOpenExclusivelyCalls); // exactly one poll, no retry
        Assert.Equal(0, env.SleepCalls);
    }

    [Fact]
    public void Apply_RelaunchExitsEarly_LogsSingleInstanceGuardComposition()
    {
        // ExitedEarly is also the exact shape produced when a relaunched game finds a single-instance guard
        // already held (a surviving sibling), focuses it, and exits itself - the log should say so.
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("game.dll")] = "v2";
        env.Files[InstallPath("Game")] = "exe";
        env.RelaunchOutcomes.Enqueue(RelaunchStartupOutcome.ExitedEarly);

        UpdateApplier.Apply(Config(new() { "game.dll" }), env);

        Assert.Contains(env.Log_, m => m.Contains("single-instance guard"));
    }
}
