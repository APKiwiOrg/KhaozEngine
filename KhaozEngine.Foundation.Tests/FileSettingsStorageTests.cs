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
    public void LoadSettings_CorruptJson_Throws()
    {
        AppDataPaths paths = TempPaths(out string root);
        try
        {
            var storage = new FileSettingsStorage(paths, new RecordingQueue());
            // File.ReadAllText succeeds; Deserialize fails. Storage does NOT catch (the manager does).
            File.WriteAllText(paths.GetFilePath("settings.json"), "not-json{{");

            Assert.Throws<JsonException>(() => storage.LoadSettings<Sample>());
        }
        finally { Cleanup(root); }
    }

    private static void Cleanup(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
