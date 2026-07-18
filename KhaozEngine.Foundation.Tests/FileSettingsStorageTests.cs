using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using KhaozEngine.App;
using KhaozEngine.Persistence;
using Xunit;

namespace KhaozEngine.Tests;

public class FileSettingsStorageTests
{
    private sealed class Sample
    {
        public int Score { get; set; }
        public string Name { get; set; } = "";
    }

    // Records enqueued writes so the storage's contract with the queue can be asserted.
    private sealed class RecordingQueue : IPersistenceQueue
    {
        public readonly List<(string Path, string Json)> Writes = new();
        public int Flushes;
        public void Enqueue(string path, string json) => Writes.Add((path, json));
        public void Flush() => Flushes++;
    }

    private static AppDataPaths TempPaths(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "ke-item10-" + Path.GetRandomFileName());
        var env = new FakeAppDataEnvironment { IsMacOS = true };
        env.Folders[Environment.SpecialFolder.ApplicationData] = root;
        return new AppDataPaths("APKiwi", "Item10Settings", env);
    }

    [Fact]
    public void Ctor_NullArgs_Throws()
    {
        AppDataPaths paths = TempPaths(out string root);
        try
        {
            Assert.Throws<ArgumentNullException>(() => new FileSettingsStorage(null!, new RecordingQueue()));
            Assert.Throws<ArgumentNullException>(() => new FileSettingsStorage(paths, null!));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void SaveSettings_EnqueuesSerializedJsonAtExpectedPath()
    {
        AppDataPaths paths = TempPaths(out string root);
        try
        {
            var queue = new RecordingQueue();
            var storage = new FileSettingsStorage(paths, queue);

            storage.SaveSettings(new Sample { Score = 7, Name = "x" });

            var write = Assert.Single(queue.Writes);
            Assert.Equal(paths.GetFilePath("settings.json"), write.Path);
            Assert.Contains("\"Score\": 7", write.Json);   // WriteIndented => "Score": 7
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void CustomSettingsFileName_IsHonored()
    {
        AppDataPaths paths = TempPaths(out string root);
        try
        {
            var queue = new RecordingQueue();
            var storage = new FileSettingsStorage(paths, queue) { SettingsFileName = "leaderboard.json" };

            storage.SaveSettings(new Sample());

            Assert.Equal(paths.GetFilePath("leaderboard.json"), Assert.Single(queue.Writes).Path);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void RoundTrip_SaveThenLoad_ReturnsEqual()
    {
        AppDataPaths paths = TempPaths(out string root);
        try
        {
            // Real async queue; Flush() forces the enqueued write to disk before the load.
            using var queue = new PersistenceQueue();
            var storage = new FileSettingsStorage(paths, queue);

            Assert.False(storage.SettingsExist());
            storage.SaveSettings(new Sample { Score = 42, Name = "neo" });
            queue.Flush();
            Assert.True(storage.SettingsExist());

            Sample loaded = storage.LoadSettings<Sample>();
            Assert.Equal(42, loaded.Score);
            Assert.Equal("neo", loaded.Name);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void LoadSettings_NoFile_ReturnsDefaults()
    {
        AppDataPaths paths = TempPaths(out string root);
        try
        {
            var storage = new FileSettingsStorage(paths, new RecordingQueue());

            Sample loaded = storage.LoadSettings<Sample>();

            Assert.Equal(0, loaded.Score);
            Assert.Equal("", loaded.Name);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void LoadSettings_CorruptEverything_ReturnsDefaults_NoThrow()
    {
        AppDataPaths paths = TempPaths(out string root);
        try
        {
            var storage = new FileSettingsStorage(paths, new RecordingQueue());
            // The ladder now catches a bad primary itself (issue #152). With no backup generations on
            // disk to fall through to, it defaults rather than throwing.
            File.WriteAllText(paths.GetFilePath("settings.json"), "not-json{{");

            Sample loaded = storage.LoadSettings<Sample>();

            Assert.Equal(0, loaded.Score);
            Assert.Equal("", loaded.Name);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void LoadSettingsDetailed_CorruptPrimary_RecoversFromBak1()
    {
        AppDataPaths paths = TempPaths(out string root);
        try
        {
            using var queue = new PersistenceQueue(backupGenerations: 2);
            var storage = new FileSettingsStorage(paths, queue);

            storage.SaveSettings(new Sample { Score = 1, Name = "gen0" });
            queue.Flush();
            storage.SaveSettings(new Sample { Score = 2, Name = "gen1" });   // rotation: score 1 now in .bak1
            queue.Flush();
            File.WriteAllText(paths.GetFilePath("settings.json"), "{ garbage");

            SaveLoadResult<Sample> result = storage.LoadSettingsDetailed<Sample>();

            Assert.Equal(SaveLoadOutcome.RecoveredFromBackup, result.Outcome);
            Assert.Equal(1, result.RecoveredGeneration);
            Assert.Equal(1, result.Value.Score);
            Assert.Equal("gen0", result.Value.Name);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void LoadSettingsDetailed_NoFile_ReturnsFreshDefault()
    {
        AppDataPaths paths = TempPaths(out string root);
        try
        {
            var storage = new FileSettingsStorage(paths, new RecordingQueue());

            SaveLoadResult<Sample> result = storage.LoadSettingsDetailed<Sample>();

            Assert.Equal(SaveLoadOutcome.FreshDefault, result.Outcome);
            Assert.Equal(0, result.RecoveredGeneration);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void LoadSettingsDetailed_CorruptPrimaryNoBackups_ReturnsRejectedAndDefaulted()
    {
        AppDataPaths paths = TempPaths(out string root);
        try
        {
            var storage = new FileSettingsStorage(paths, new RecordingQueue()) { BackupGenerations = 0 };
            File.WriteAllText(paths.GetFilePath("settings.json"), "{ garbage");

            SaveLoadResult<Sample> result = storage.LoadSettingsDetailed<Sample>();

            Assert.Equal(SaveLoadOutcome.RejectedAndDefaulted, result.Outcome);
            Assert.NotNull(result.Detail);
            Assert.Equal(0, result.Value.Score);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void LoadSettingsDetailed_NegativeBackupGenerations_StillProbesPrimary()
    {
        AppDataPaths paths = TempPaths(out string root);
        try
        {
            var storage = new FileSettingsStorage(paths, new RecordingQueue()) { BackupGenerations = -1 };
            using var queue = new PersistenceQueue();
            var writingStorage = new FileSettingsStorage(paths, queue);
            writingStorage.SaveSettings(new Sample { Score = 3, Name = "primary" });
            queue.Flush();

            SaveLoadResult<Sample> result = storage.LoadSettingsDetailed<Sample>();

            // A negative BackupGenerations must not skip generation 0 (the primary): clamped to 0, not
            // treated as "probe nothing".
            Assert.Equal(SaveLoadOutcome.Loaded, result.Outcome);
            Assert.Equal(3, result.Value.Score);
            Assert.Equal("primary", result.Value.Name);
        }
        finally { Cleanup(root); }
    }

    private static void Cleanup(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
