using System;
using System.IO;
using System.Text.Json;
using KhaozEngine.App;
using KhaozEngine.Persistence;
using Xunit;

namespace KhaozEngine.Tests;

public class AtomicJsonWriterTests
{
    private sealed record Sample(string Name, int Value);

    [Fact]
    public void WriteText_CreatesFileWithContents()
    {
        string root = NewTempRoot();
        try
        {
            string path = Path.Combine(root, "a.json");
            AtomicJsonWriter.WriteText(path, "hello");
            Assert.Equal("hello", File.ReadAllText(path));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void WriteText_OverwritesExistingFile()
    {
        string root = NewTempRoot();
        try
        {
            string path = Path.Combine(root, "a.json");
            AtomicJsonWriter.WriteText(path, "first");
            AtomicJsonWriter.WriteText(path, "second");
            Assert.Equal("second", File.ReadAllText(path));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void WriteText_CreatesMissingParentDirectory()
    {
        string root = NewTempRoot();
        try
        {
            string path = Path.Combine(root, "nested", "deep", "a.json");
            AtomicJsonWriter.WriteText(path, "x");
            Assert.True(File.Exists(path));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void WriteText_LeavesNoTempFile()
    {
        string root = NewTempRoot();
        try
        {
            string path = Path.Combine(root, "a.json");
            AtomicJsonWriter.WriteText(path, "x");
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Write_RoundTripsValueAsIndentedJson()
    {
        string root = NewTempRoot();
        try
        {
            string path = Path.Combine(root, "a.json");
            var value = new Sample("n", 7);
            AtomicJsonWriter.Write(path, value);
            string json = File.ReadAllText(path);
            Assert.Contains("\n", json); // WriteIndented default
            Sample? back = JsonSerializer.Deserialize<Sample>(json);
            Assert.Equal(value, back);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void WriteText_AppDataPathsOverload_WritesToResolvedFilePath()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment { IsWindows = true };
            env.Folders[Environment.SpecialFolder.ApplicationData] = root;
            var paths = new AppDataPaths("APKiwi", "MyGame", env);

            AtomicJsonWriter.WriteText(paths, "save.json", "data");

            Assert.Equal("data", File.ReadAllText(paths.GetFilePath("save.json")));
        }
        finally { Cleanup(root); }
    }

    private static string NewTempRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ke-atomicwriter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch { /* best effort */ }
    }
}
