using System;
using System.IO;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests;

public class FileLoggerTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "khaozengine-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void LogPathIsNullBeforeInitialize()
    {
        using var log = new FileLogger();
        Assert.Null(log.LogPath);
    }

    [Fact]
    public void InitializeCreatesFileAndExposesPath()
    {
        string dir = TempDir();
        string logPath = Path.Combine(dir, "game.log");
        try
        {
            using var log = new FileLogger();
            log.Initialize(logPath);
            Assert.Equal(logPath, log.LogPath);
            Assert.True(File.Exists(logPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void InfoWritesLevelTaggedLine()
    {
        string dir = TempDir();
        string logPath = Path.Combine(dir, "game.log");
        try
        {
            var log = new FileLogger();
            log.Initialize(logPath);
            log.Info("hello world");
            log.Shutdown();

            string text = File.ReadAllText(logPath);
            Assert.Contains("[INFO] hello world", text);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void WarnAndErrorUseTheirOwnTags()
    {
        string dir = TempDir();
        string logPath = Path.Combine(dir, "game.log");
        try
        {
            var log = new FileLogger();
            log.Initialize(logPath);
            log.Warn("careful");
            log.Error("boom");
            log.Shutdown();

            string text = File.ReadAllText(logPath);
            Assert.Contains("[WARN] careful", text);
            Assert.Contains("[ERROR] boom", text);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ErrorWithExceptionIncludesExceptionText()
    {
        string dir = TempDir();
        string logPath = Path.Combine(dir, "game.log");
        try
        {
            var log = new FileLogger();
            log.Initialize(logPath);
            log.Error("save failed", new InvalidOperationException("disk gone"));
            log.Shutdown();

            string text = File.ReadAllText(logPath);
            Assert.Contains("[ERROR] save failed", text);
            Assert.Contains("disk gone", text);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void InitializeRotatesExistingLogToPreviousPath()
    {
        string dir = TempDir();
        string logPath = Path.Combine(dir, "game.log");
        string prevPath = Path.Combine(dir, "game.prev.log");
        try
        {
            // First session leaves a marker line.
            var first = new FileLogger();
            first.Initialize(logPath, prevPath);
            first.Info("from first session");
            first.Shutdown();

            // Second session must rotate the first session's log into prev.
            var second = new FileLogger();
            second.Initialize(logPath, prevPath);
            second.Info("from second session");
            second.Shutdown();

            Assert.True(File.Exists(prevPath));
            Assert.Contains("from first session", File.ReadAllText(prevPath));

            string current = File.ReadAllText(logPath);
            Assert.Contains("from second session", current);
            Assert.DoesNotContain("from first session", current);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void InitializeWithoutPreviousPathDoesNotRotate()
    {
        string dir = TempDir();
        string logPath = Path.Combine(dir, "game.log");
        string prevPath = Path.Combine(dir, "game.prev.log");
        try
        {
            var first = new FileLogger();
            first.Initialize(logPath);
            first.Info("session one");
            first.Shutdown();

            var second = new FileLogger();
            second.Initialize(logPath);   // no previous path supplied
            second.Shutdown();

            Assert.False(File.Exists(prevPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void SecondInitializeIsIgnoredWhileActive()
    {
        string dir = TempDir();
        string firstPath = Path.Combine(dir, "first.log");
        string secondPath = Path.Combine(dir, "second.log");
        try
        {
            using var log = new FileLogger();
            log.Initialize(firstPath);
            log.Initialize(secondPath);   // must be ignored while already open
            Assert.Equal(firstPath, log.LogPath);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LoggingAfterShutdownDoesNotThrow()
    {
        string dir = TempDir();
        string logPath = Path.Combine(dir, "game.log");
        try
        {
            var log = new FileLogger();
            log.Initialize(logPath);
            log.Shutdown();
            log.Info("after shutdown");   // best-effort no-op, must not throw
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
