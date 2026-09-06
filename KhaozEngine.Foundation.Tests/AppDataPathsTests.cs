using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.App;
using Xunit;

namespace KhaozEngine.Tests;

public class AppDataPathsTests
{
    private const string Publisher = "APKiwi";
    private const string AppName = "MyGame";

    [Fact]
    public void BaseDirectory_Windows_UsesApplicationDataUnderPublisher()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment { IsWindows = true };
            env.Folders[Environment.SpecialFolder.ApplicationData] = root;

            var paths = new AppDataPaths(Publisher, AppName, env);

            Assert.Equal(Path.Combine(root, Publisher, AppName), paths.BaseDirectory);
            Assert.True(Directory.Exists(paths.BaseDirectory));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void BaseDirectory_MacOS_UsesApplicationDataUnderPublisher()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment { IsMacOS = true };
            env.Folders[Environment.SpecialFolder.ApplicationData] = root;

            var paths = new AppDataPaths(Publisher, AppName, env);

            Assert.Equal(Path.Combine(root, Publisher, AppName), paths.BaseDirectory);
            Assert.True(Directory.Exists(paths.BaseDirectory));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void BaseDirectory_Linux_UsesXdgDataHomeWhenSet()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment { IsLinux = true };
            env.EnvVars["XDG_DATA_HOME"] = root;

            var paths = new AppDataPaths(Publisher, AppName, env);

            Assert.Equal(Path.Combine(root, Publisher, AppName), paths.BaseDirectory);
            Assert.True(Directory.Exists(paths.BaseDirectory));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void BaseDirectory_Linux_FallsBackToHomeLocalShareWhenNoXdg()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment { IsLinux = true };
            env.EnvVars["HOME"] = root; // XDG_DATA_HOME deliberately absent

            var paths = new AppDataPaths(Publisher, AppName, env);

            Assert.Equal(Path.Combine(root, ".local", "share", Publisher, AppName), paths.BaseDirectory);
            Assert.True(Directory.Exists(paths.BaseDirectory));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void BaseDirectory_NoOsMatch_FallsBackToLocalApplicationData()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment();
            env.Folders[Environment.SpecialFolder.LocalApplicationData] = root;

            var paths = new AppDataPaths(Publisher, AppName, env);

            Assert.Equal(Path.Combine(root, Publisher, AppName), paths.BaseDirectory);
            Assert.True(Directory.Exists(paths.BaseDirectory));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void BaseDirectory_LastResort_UsesUserProfileDotPublisherThenAppName()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment();
            env.Folders[Environment.SpecialFolder.UserProfile] = root;

            var paths = new AppDataPaths(Publisher, AppName, env);

            Assert.Equal(Path.Combine(root, "." + Publisher.ToLowerInvariant(), AppName), paths.BaseDirectory);
            Assert.True(Directory.Exists(paths.BaseDirectory));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void BaseDirectory_OsBranchWithBlankPath_FallsThroughToLocalApplicationData()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment { IsWindows = true };
            env.Folders[Environment.SpecialFolder.ApplicationData] = "   ";
            env.Folders[Environment.SpecialFolder.LocalApplicationData] = root;

            var paths = new AppDataPaths(Publisher, AppName, env);

            Assert.Equal(Path.Combine(root, Publisher, AppName), paths.BaseDirectory);
            Assert.True(Directory.Exists(paths.BaseDirectory));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void BaseDirectory_ResolvesOnceAndCaches()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment { IsWindows = true };
            env.Folders[Environment.SpecialFolder.ApplicationData] = root;

            var paths = new AppDataPaths(Publisher, AppName, env);

            string first = paths.BaseDirectory;
            string second = paths.BaseDirectory;

            Assert.Equal(first, second);
            Assert.Equal(1, env.GetFolderPathCalls);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void BaseDirectory_Android_UsesLocalApplicationDataSandboxUnderPublisher()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment { IsAndroid = true };
            env.Folders[Environment.SpecialFolder.LocalApplicationData] = root;

            var paths = new AppDataPaths(Publisher, AppName, env);

            Assert.Equal(Path.Combine(root, Publisher, AppName), paths.BaseDirectory);
            Assert.True(Directory.Exists(paths.BaseDirectory));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void BaseDirectory_IOS_UsesLocalApplicationDataSandboxUnderPublisher()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment { IsIOS = true };
            env.Folders[Environment.SpecialFolder.LocalApplicationData] = root;

            var paths = new AppDataPaths(Publisher, AppName, env);

            Assert.Equal(Path.Combine(root, Publisher, AppName), paths.BaseDirectory);
            Assert.True(Directory.Exists(paths.BaseDirectory));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void BaseDirectory_Android_TakesPrecedenceOverDesktopFlag()
    {
        string sandbox = NewTempRoot();
        string desktop = NewTempRoot();
        try
        {
            // Both Android and a desktop flag set: the mobile sandbox must win.
            var env = new FakeAppDataEnvironment { IsAndroid = true, IsLinux = true };
            env.Folders[Environment.SpecialFolder.LocalApplicationData] = sandbox;
            env.EnvVars["XDG_DATA_HOME"] = desktop;

            var paths = new AppDataPaths(Publisher, AppName, env);

            Assert.Equal(Path.Combine(sandbox, Publisher, AppName), paths.BaseDirectory);
        }
        finally { Cleanup(sandbox); Cleanup(desktop); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Ctor_InvalidPublisher_Throws(string? badPublisher)
    {
        Assert.Throws<ArgumentException>(() => new AppDataPaths(badPublisher!, AppName, new FakeAppDataEnvironment()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Ctor_InvalidAppName_Throws(string? badAppName)
    {
        Assert.Throws<ArgumentException>(() => new AppDataPaths(Publisher, badAppName!, new FakeAppDataEnvironment()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/path")]
    public void FromDirectory_RejectsMissingOrRelativePaths(string? badDirectory)
    {
        Assert.Throws<ArgumentException>(() => AppDataPaths.FromDirectory(badDirectory!));
    }

    [Fact]
    public void FromDirectory_NormalizesAndLazilyCreatesOnlyTheChosenDirectory()
    {
        string root = NewTempRoot();
        string chosen = Path.Combine(root, "intermediate", "..", "chosen");
        string expected = Path.Combine(root, "chosen");
        try
        {
            Directory.CreateDirectory(root);

            AppDataPaths paths = AppDataPaths.FromDirectory(chosen);

            Assert.False(Directory.Exists(expected));
            Assert.Equal(expected, paths.BaseDirectory);
            Assert.True(Directory.Exists(expected));
            Assert.Equal(Path.Combine(expected, "settings.json"), paths.SettingsFilePath);
            Assert.False(Directory.Exists(Path.Combine(root, Publisher, AppName)));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void FilePaths_ComposeOffBaseDirectory()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment { IsWindows = true };
            env.Folders[Environment.SpecialFolder.ApplicationData] = root;

            var paths = new AppDataPaths(Publisher, AppName, env);
            string baseDir = paths.BaseDirectory;

            Assert.Equal(Path.Combine(baseDir, "save.json"), paths.SaveFilePath);
            Assert.Equal(Path.Combine(baseDir, "settings.json"), paths.SettingsFilePath);
            Assert.Equal(Path.Combine(baseDir, "game.log"), paths.LogFilePath);
            Assert.Equal(Path.Combine(baseDir, "game.prev.log"), paths.PreviousLogFilePath);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void GetFilePath_ComposesArbitraryNameOffBaseDirectory()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment { IsWindows = true };
            env.Folders[Environment.SpecialFolder.ApplicationData] = root;

            var paths = new AppDataPaths(Publisher, AppName, env);

            Assert.Equal(Path.Combine(paths.BaseDirectory, "custom.dat"), paths.GetFilePath("custom.dat"));
        }
        finally { Cleanup(root); }
    }

    // --- helpers ---

    private static string NewTempRoot() =>
        Path.Combine(Path.GetTempPath(), "KhaozEngineAppDataTests", Guid.NewGuid().ToString("N"));

    private static void Cleanup(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch { /* best-effort */ }
    }
}

/// <summary>Test double for <see cref="IAppDataEnvironment"/> - all facts are settable.</summary>
internal sealed class FakeAppDataEnvironment : IAppDataEnvironment
{
    public bool IsWindows { get; set; }
    public bool IsMacOS { get; set; }
    public bool IsLinux { get; set; }
    public bool IsAndroid { get; set; }
    public bool IsIOS { get; set; }
    public Dictionary<Environment.SpecialFolder, string> Folders { get; } = new();
    public Dictionary<string, string?> EnvVars { get; } = new();
    public int GetFolderPathCalls { get; private set; }

    public string GetFolderPath(Environment.SpecialFolder folder)
    {
        GetFolderPathCalls++;
        return Folders.TryGetValue(folder, out string? value) ? value : string.Empty;
    }

    public string? GetEnvironmentVariable(string variable) =>
        EnvVars.TryGetValue(variable, out string? value) ? value : null;
}
