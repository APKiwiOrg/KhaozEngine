using System;
using System.IO;
using KhaozEngine.App;
using KhaozEngine.Persistence;
using Xunit;

namespace KhaozEngine.Tests.Persistence;

public class FileSettingsStorageTolerantReadTests
{
    private sealed class Settings { public int Volume { get; set; } }

    private sealed class RecordingQueue : IPersistenceQueue
    {
        public void Enqueue(string path, string json) { }
        public void Flush() { }
    }

    private static AppDataPaths TempPaths(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "ke-tolerant-" + Path.GetRandomFileName());
        var env = new FakeAppDataEnvironment { IsMacOS = true };
        env.Folders[Environment.SpecialFolder.ApplicationData] = root;
        return new AppDataPaths("APKiwi", "TolerantReadTest", env);
    }

    [Fact]
    public void Load_AcceptsCommentsAndTrailingComma()
    {
        AppDataPaths paths = TempPaths(out string root);
        try
        {
            var storage = new FileSettingsStorage(paths, new RecordingQueue());
            // Write a hand-edited JSON with a comment and trailing comma directly to the expected path.
            string filePath = paths.GetFilePath("settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, "{\n  // user edit\n  \"Volume\": 7,\n}");

            Settings s = storage.LoadSettings<Settings>();

            Assert.Equal(7, s.Volume);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Load_AcceptsMixedCasePropertyName()
    {
        AppDataPaths paths = TempPaths(out string root);
        try
        {
            var storage = new FileSettingsStorage(paths, new RecordingQueue());
            string filePath = paths.GetFilePath("settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, "{ \"volume\": 5 }");

            Settings s = storage.LoadSettings<Settings>();

            Assert.Equal(5, s.Volume);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
