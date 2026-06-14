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
}
