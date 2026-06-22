using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using KhaozEngine.Updates;
using Xunit;

namespace KhaozEngine.Tests.Updates;

/// <summary>
/// Covers the self-relocation flow that lets the updater overwrite its own binaries on Windows (where a
/// running process locks its loaded .exe/.dll). Stage 1 copies the updater's closure to a scratch dir and
/// re-launches from there; stage 2 (<c>--relocated</c>) applies in place and schedules the scratch cleanup.
/// </summary>
public sealed class UpdaterRelocationTests
{
    private const string Install = "/install";
    private const string AppData = "/appdata";

    // A deps.json whose package-dependency runtime keys are package-relative (lib/<tfm>/...), exercising
    // the flatten-to-filename resolution. The app's own runtime key is already a bare filename.
    private const string SampleDeps = """
    {
      "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0" },
      "targets": {
        ".NETCoreApp,Version=v10.0": {
          "HardpointUpdater/1.0.0": {
            "dependencies": { "KhaozEngine.Updates": "7.3.0" },
            "runtime": { "HardpointUpdater.dll": {} }
          },
          "KhaozEngine.Updates/7.3.0": {
            "dependencies": { "KhaozEngine.Diagnostics": "7.3.0" },
            "runtime": { "lib/net10.0/KhaozEngine.Updates.dll": {} }
          },
          "KhaozEngine.Diagnostics/7.3.0": {
            "runtime": { "lib/net10.0/KhaozEngine.Diagnostics.dll": {} }
          }
        }
      }
    }
    """;

    private static string InstallPath(string rel) => Path.Combine(Install, rel.Replace('/', Path.DirectorySeparatorChar));
    private static string StagingPath(string rel) => Path.Combine("/staging", rel.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void ResolveUpdaterClosure_IncludesHostQuartetAndFlattenedDeps()
    {
        IReadOnlyList<string> closure = UpdateApplier.ResolveUpdaterClosure(SampleDeps, "HardpointUpdater.exe");

        Assert.Contains("HardpointUpdater.exe", closure);
        Assert.Contains("HardpointUpdater.dll", closure);
        Assert.Contains("HardpointUpdater.runtimeconfig.json", closure);
        Assert.Contains("HardpointUpdater.deps.json", closure);
        Assert.Contains("KhaozEngine.Updates.dll", closure);       // flattened from lib/net10.0/...
        Assert.Contains("KhaozEngine.Diagnostics.dll", closure);
        Assert.DoesNotContain("lib/net10.0/KhaozEngine.Updates.dll", closure); // package path not used verbatim
    }

    [Fact]
    public void ResolveUpdaterClosure_MalformedDeps_ReturnsHostQuartetOnly()
    {
        IReadOnlyList<string> closure = UpdateApplier.ResolveUpdaterClosure("not json {", "HardpointUpdater.exe");

        Assert.Equal(
            new[] { "HardpointUpdater.exe", "HardpointUpdater.dll", "HardpointUpdater.runtimeconfig.json", "HardpointUpdater.deps.json" },
            closure);
    }

    [Fact]
    public void ResolveUpdaterClosure_PosixApphostNoExtension_StillIncludesHost()
    {
        IReadOnlyList<string> closure = UpdateApplier.ResolveUpdaterClosure("", "HardpointUpdater");

        Assert.Contains("HardpointUpdater", closure);              // apphost (no extension)
        Assert.Contains("HardpointUpdater.dll", closure);
        Assert.Contains("HardpointUpdater.runtimeconfig.json", closure);
        Assert.Contains("HardpointUpdater.deps.json", closure);
    }

    [Fact]
    public void Run_UpdaterInsideInstall_RelocatesClosureAndHandsOffWithoutApplying()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ke-reloc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string configPath = Path.Combine(dir, "apply-update.json");
        try
        {
            var env = new FakeUpdaterEnvironment
            {
                SelfExePath = Path.Combine(Install, "HardpointUpdater.exe"),
                SelfBaseDir = Install,
            };
            // Updater closure sources present in the install dir.
            env.Files[InstallPath("HardpointUpdater.exe")] = "host";
            env.Files[InstallPath("HardpointUpdater.dll")] = "updater-asm";
            env.Files[InstallPath("HardpointUpdater.runtimeconfig.json")] = "{}";
            env.Files[InstallPath("HardpointUpdater.deps.json")] = SampleDeps;
            env.Files[InstallPath("KhaozEngine.Updates.dll")] = "updates";
            env.Files[InstallPath("KhaozEngine.Diagnostics.dll")] = "diag";
            // An install file that must NOT be touched while we are only relocating (stage 1).
            env.Files[InstallPath("Hardpoint.Core.dll")] = "v1";

            WriteConfig(configPath);

            int exit = UpdateApplier.Run(new[] { "--apply", configPath }, env);

            Assert.Equal(0, exit);

            string relocateDir = Path.Combine(AppData, "updater-relocate", "2.0.0");
            // Handed off to the relocated copy with the same config.
            Assert.Equal(Path.Combine(relocateDir, "HardpointUpdater.exe"), env.RelocatedExe);
            Assert.Equal(configPath, env.RelocatedConfig);
            Assert.Equal(relocateDir, env.RelocatedWorkdir);
            // Closure (including the self-locking updater dll + engine deps) copied into the scratch dir.
            Assert.Equal("updater-asm", env.Files[Path.Combine(relocateDir, "HardpointUpdater.dll")]);
            Assert.Equal("updates", env.Files[Path.Combine(relocateDir, "KhaozEngine.Updates.dll")]);
            Assert.Equal("diag", env.Files[Path.Combine(relocateDir, "KhaozEngine.Diagnostics.dll")]);
            // Stage 1 did NOT apply: install untouched, config kept for stage 2, no relaunch.
            Assert.Equal("v1", env.Files[InstallPath("Hardpoint.Core.dll")]);
            Assert.Null(env.RelaunchedExe);
            Assert.True(File.Exists(configPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Run_Relocated_AppliesInPlaceAndSchedulesScratchCleanup()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ke-reloc2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string configPath = Path.Combine(dir, "apply-update.json");
        try
        {
            string scratch = Path.Combine(AppData, "updater-relocate", "2.0.0");
            var env = new FakeUpdaterEnvironment
            {
                // Stage 2 runs from the scratch dir (outside the install dir).
                SelfExePath = Path.Combine(scratch, "HardpointUpdater.exe"),
                SelfBaseDir = scratch,
            };
            env.Files[StagingPath("game.dll")] = "v2";
            env.Files[InstallPath("Game")] = "exe";

            WriteConfig(configPath);

            int exit = UpdateApplier.Run(new[] { "--apply", configPath, "--relocated" }, env);

            Assert.Equal(0, exit);
            Assert.Equal("v2", env.Files[InstallPath("game.dll")]);  // applied in place
            Assert.Equal(InstallPath("Game"), env.RelaunchedExe);     // relaunched the game
            Assert.Contains(scratch, env.ScheduledDeletions);         // scratch dir cleanup scheduled
            Assert.Null(env.RelocatedExe);                            // did NOT relocate a second time
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Run_UpdaterOutsideInstall_AppliesInPlaceWithoutRelocating()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ke-reloc3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string configPath = Path.Combine(dir, "apply-update.json");
        try
        {
            var env = new FakeUpdaterEnvironment
            {
                SelfExePath = Path.Combine("/somewhere-else", "HardpointUpdater.exe"),
                SelfBaseDir = "/somewhere-else",
            };
            env.Files[StagingPath("game.dll")] = "v2";
            env.Files[InstallPath("Game")] = "exe";

            WriteConfig(configPath);

            int exit = UpdateApplier.Run(new[] { "--apply", configPath }, env);

            Assert.Equal(0, exit);
            Assert.Equal("v2", env.Files[InstallPath("game.dll")]);  // applied in place
            Assert.Null(env.RelocatedExe);                            // no relocation (already outside install)
            Assert.Empty(env.ScheduledDeletions);                     // not relocated, so nothing to clean
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void WriteConfig(string configPath)
    {
        var config = new ApplyUpdateConfig
        {
            TargetVersion = "2.0.0",
            InstallDir = Install,
            StagingDir = "/staging",
            FilesToCopy = new List<string> { "game.dll" },
            GameExePath = InstallPath("Game"),
            ParentPid = 1234,
            ManifestDestPath = Path.Combine(AppData, "update-manifest.json"),
            AppDataDir = AppData,
        };
        File.WriteAllText(configPath, JsonSerializer.Serialize(config));
    }
}
