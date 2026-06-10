using System;
using System.IO;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Logging;

public class FileSinkTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "khaoz-filesink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static LogEntry Entry(string msg, LogLevel level = LogLevel.Info) =>
        new(new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero), level, "Cat", msg);

    [Fact]
    public void EmitWritesFormattedLineToFile()
    {
        string dir = TempDir();
        string path = Path.Combine(dir, "game.log");
        try
        {
            using (var sink = new FileSink(new FileSinkOptions { Path = path }))
            {
                sink.Emit(Entry("hello"));
            }
            Assert.Contains("[INFO] [Cat] hello", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void RotatesExistingLogToPreviousPathOnOpen()
    {
        string dir = TempDir();
        string path = Path.Combine(dir, "game.log");
        string prev = Path.Combine(dir, "game.prev.log");
        try
        {
            using (var first = new FileSink(new FileSinkOptions { Path = path, PreviousPath = prev }))
                first.Emit(Entry("session one"));
            using (var second = new FileSink(new FileSinkOptions { Path = path, PreviousPath = prev }))
                second.Emit(Entry("session two"));

            Assert.Contains("session one", File.ReadAllText(prev));
            string current = File.ReadAllText(path);
            Assert.Contains("session two", current);
            Assert.DoesNotContain("session one", current);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void WithoutPreviousPathDoesNotRotate()
    {
        string dir = TempDir();
        string path = Path.Combine(dir, "game.log");
        string prev = Path.Combine(dir, "game.prev.log");
        try
        {
            using (var first = new FileSink(new FileSinkOptions { Path = path }))
                first.Emit(Entry("one"));
            using (var second = new FileSink(new FileSinkOptions { Path = path }))
                second.Emit(Entry("two"));
            Assert.False(File.Exists(prev));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SizeBasedRotationCreatesArchivesAndPrunesToMaxFiles()
    {
        string dir = TempDir();
        string path = Path.Combine(dir, "game.log");
        try
        {
            // Small MaxBytes so each ~30-byte line forces a roll; keep at most 2 archives.
            var options = new FileSinkOptions { Path = path, MaxBytes = 20, MaxFiles = 2 };
            using (var sink = new FileSink(options))
            {
                for (int i = 0; i < 5; i++) sink.Emit(Entry("line" + i));
            }

            Assert.True(File.Exists(path));                       // active
            Assert.True(File.Exists(path + ".1"));                // newest archive
            Assert.True(File.Exists(path + ".2"));                // older archive
            Assert.False(File.Exists(path + ".3"));               // pruned beyond MaxFiles
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void BelowSinkMinimumLevelIsSkipped()
    {
        string dir = TempDir();
        string path = Path.Combine(dir, "game.log");
        try
        {
            using (var sink = new FileSink(new FileSinkOptions { Path = path, MinimumLevel = LogLevel.Error }))
            {
                sink.Emit(Entry("verbose", LogLevel.Info));
                sink.Emit(Entry("boom", LogLevel.Error));
            }
            string text = File.ReadAllText(path);
            Assert.DoesNotContain("verbose", text);
            Assert.Contains("boom", text);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void EmitOnUnwritablePathNeverThrows()
    {
        // A directory that cannot be created (path under an existing file) must not throw.
        string dir = TempDir();
        string fileAsDir = Path.Combine(dir, "afile");
        File.WriteAllText(fileAsDir, "x");
        string badPath = Path.Combine(fileAsDir, "nested", "game.log");
        try
        {
            using var sink = new FileSink(new FileSinkOptions { Path = badPath });
            var ex = Record.Exception(() => sink.Emit(Entry("hello")));
            Assert.Null(ex);
        }
        finally { Directory.Delete(dir, true); }
    }
}
