using System;
using System.IO;
using System.Linq;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Logging;

// Configure() mutates the ambient Log, installs CrashHandler, and (by default) writes the console: run serially.
[Collection("LoggingSerial")]
public class SessionLogTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "khaoz-sessionlog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Prune_KeepsNewestRetainedMinusOne_ByLastWrite()
    {
        string dir = TempDir();
        try
        {
            var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < 12; i++)
            {
                string p = Path.Combine(dir, $"session-{i:00}.log");
                File.WriteAllText(p, "x");
                File.SetLastWriteTimeUtc(p, baseTime.AddMinutes(i)); // i = 11 is newest
            }

            SessionLog.PruneOldSessionLogs(dir, maxRetained: 5, prefix: "session");

            string[] remaining = Directory.GetFiles(dir, "session-*.log")
                .Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;
            // Keeps maxRetained - 1 = 4 newest, so Configure's own file lands the dir at exactly 5.
            Assert.Equal(new[] { "session-08.log", "session-09.log", "session-10.log", "session-11.log" }, remaining);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Prune_OnlyTouchesMatchingPrefix()
    {
        string dir = TempDir();
        try
        {
            for (int i = 0; i < 6; i++) File.WriteAllText(Path.Combine(dir, $"session-{i}.log"), "x");
            File.WriteAllText(Path.Combine(dir, "keep.log"), "x");
            File.WriteAllText(Path.Combine(dir, "server-1.log"), "x");

            SessionLog.PruneOldSessionLogs(dir, maxRetained: 2, prefix: "session");

            Assert.True(File.Exists(Path.Combine(dir, "keep.log")));
            Assert.True(File.Exists(Path.Combine(dir, "server-1.log")));
            Assert.Single(Directory.GetFiles(dir, "session-*.log")); // maxRetained - 1
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Prune_MissingDirectory_DoesNotThrow()
    {
        string missing = Path.Combine(Path.GetTempPath(), "khaoz-sessionlog-missing-" + Guid.NewGuid().ToString("N"));
        Assert.Null(Record.Exception(() => SessionLog.PruneOldSessionLogs(missing, 10, "session")));
    }

    [Fact]
    public void Configure_OpensTimestampedFile_WritesIdentityLine_AndReturnsPath()
    {
        string dir = TempDir();
        try
        {
            string path = SessionLog.Configure(new SessionLogOptions
            {
                Directory = dir,
                ProcessLabel = "TestGame.Server",
                BuildVersion = "0.4.2",
                Console = false, // keep the test console quiet
            });
            Log.Flush();

            Assert.StartsWith(Path.Combine(dir, "session-"), path);
            Assert.EndsWith(".log", path);
            Assert.True(File.Exists(path));

            string body = File.ReadAllText(path);
            Assert.Contains("TestGame.Server", body);
            Assert.Contains("0.4.2", body);          // game build version
            Assert.Contains("KhaozEngine ", body);   // engine version, read off the engine assembly
            Assert.Contains("session log:", body);
        }
        finally { Reset(dir); }
    }

    [Fact]
    public void Configure_OmitsBuildSegment_WhenNoBuildVersion()
    {
        string dir = TempDir();
        try
        {
            string path = SessionLog.Configure(dir, "NoVersionGame");
            Log.Flush();

            string body = File.ReadAllText(path);
            Assert.Contains("NoVersionGame | KhaozEngine ", body); // no build segment between label and engine
        }
        finally { Reset(dir); }
    }

    [Fact]
    public void Configure_InstalledCrashHandler_RoutesFatalToSessionFile()
    {
        string dir = TempDir();
        try
        {
            string path = SessionLog.Configure(new SessionLogOptions { Directory = dir, ProcessLabel = "CrashProbe", Console = false });
            CrashHandler.Report("Unhandled exception (terminating)", new InvalidOperationException("boom"), null);
            Log.Flush();

            string body = File.ReadAllText(path);
            Assert.Contains("[FATAL]", body);
            Assert.Contains("boom", body);
        }
        finally { Reset(dir); }
    }

    [Fact]
    public void Configure_CapsRetainedSessions()
    {
        string dir = TempDir();
        try
        {
            var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < 8; i++)
            {
                string p = Path.Combine(dir, $"session-old-{i:00}.log");
                File.WriteAllText(p, "x");
                File.SetLastWriteTimeUtc(p, baseTime.AddMinutes(i));
            }

            SessionLog.Configure(new SessionLogOptions { Directory = dir, ProcessLabel = "Cap", MaxRetainedSessions = 3, Console = false });

            // 2 newest pre-existing (maxRetained - 1) plus the one Configure just opened.
            Assert.Equal(3, Directory.GetFiles(dir, "session-*.log").Length);
        }
        finally { Reset(dir); }
    }

    [Fact]
    public void Configure_NullOptions_Throws()
        => Assert.Throws<ArgumentNullException>(() => SessionLog.Configure((SessionLogOptions)null!));

    [Fact]
    public void Configure_BlankDirectory_Throws()
        => Assert.Throws<ArgumentException>(() => SessionLog.Configure(new SessionLogOptions { Directory = "   " }));

    private static void Reset(string dir)
    {
        Log.Shutdown();
        CrashHandler.Uninstall();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
