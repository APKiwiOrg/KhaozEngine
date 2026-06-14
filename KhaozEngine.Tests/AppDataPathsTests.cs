using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.App;
using Xunit;

namespace KhaozEngine.Tests;

public class AppDataPathsTests
{
    private const string AppFolder = "MyGame";

    [Fact]
    public void BaseDirectory_Windows_UsesApplicationData()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment { IsWindows = true };
            env.Folders[Environment.SpecialFolder.ApplicationData] = root;

            var paths = new AppDataPaths(AppFolder, env);

            Assert.Equal(Path.Combine(root, AppFolder), paths.BaseDirectory);
            Assert.True(Directory.Exists(paths.BaseDirectory));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void BaseDirectory_MacOS_UsesApplicationData()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment { IsMacOS = true };
            env.Folders[Environment.SpecialFolder.ApplicationData] = root;

            var paths = new AppDataPaths(AppFolder, env);

            Assert.Equal(Path.Combine(root, AppFolder), paths.BaseDirectory);
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

            var paths = new AppDataPaths(AppFolder, env);

            Assert.Equal(Path.Combine(root, AppFolder), paths.BaseDirectory);
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

            var paths = new AppDataPaths(AppFolder, env);

            Assert.Equal(Path.Combine(root, ".local", "share", AppFolder), paths.BaseDirectory);
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
            // No OS flag set; primary branch never taken.
            var env = new FakeAppDataEnvironment();
            env.Folders[Environment.SpecialFolder.LocalApplicationData] = root;

            var paths = new AppDataPaths(AppFolder, env);

            Assert.Equal(Path.Combine(root, AppFolder), paths.BaseDirectory);
            Assert.True(Directory.Exists(paths.BaseDirectory));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void BaseDirectory_LastResort_UsesUserProfileDotFolder()
    {
        string root = NewTempRoot();
        try
        {
            // Nothing resolves except UserProfile.
            var env = new FakeAppDataEnvironment();
            env.Folders[Environment.SpecialFolder.UserProfile] = root;

            var paths = new AppDataPaths(AppFolder, env);

            Assert.Equal(Path.Combine(root, "." + AppFolder.ToLowerInvariant()), paths.BaseDirectory);
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
            // Windows is detected, but ApplicationData resolves to whitespace, so the OS branch
            // must fall through to the LocalApplicationData fallback rather than return a bad path.
            var env = new FakeAppDataEnvironment { IsWindows = true };
            env.Folders[Environment.SpecialFolder.ApplicationData] = "   ";
            env.Folders[Environment.SpecialFolder.LocalApplicationData] = root;

            var paths = new AppDataPaths(AppFolder, env);

            Assert.Equal(Path.Combine(root, AppFolder), paths.BaseDirectory);
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

            var paths = new AppDataPaths(AppFolder, env);

            string first = paths.BaseDirectory;
            string second = paths.BaseDirectory;

            Assert.Equal(first, second);
            Assert.Equal(1, env.GetFolderPathCalls); // resolution happened exactly once
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Ctor_InvalidAppFolderName_Throws(string? badName)
    {
        Assert.Throws<ArgumentException>(() => new AppDataPaths(badName!, new FakeAppDataEnvironment()));
    }

    [Fact]
    public void FilePaths_ComposeOffBaseDirectory()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment { IsWindows = true };
            env.Folders[Environment.SpecialFolder.ApplicationData] = root;

            var paths = new AppDataPaths(AppFolder, env);
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

            var paths = new AppDataPaths(AppFolder, env);

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
