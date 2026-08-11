using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Logging;

/// <summary>
/// THE CRASH FILE'S SHAPE AND ITS WRITER, driven with a fabricated exception rather than a real crash.
/// <see cref="CrashReport"/> exists because a one-off unhandled exception went to a terminal and was gone by the
/// time anyone asked what it said (https://github.com/APKiwiOrg/KhaozEngine/issues/607), so what these rows pin
/// is that the file carries the facts that investigation wanted: the exception's type, its message, its stack,
/// when it happened, which engine version, and which graphics backend.
/// <para>
/// Serial, because arming the report and the ambient notes are both process-global, exactly like
/// <see cref="CrashHandler"/> next door.
/// </para>
/// </summary>
[Collection("LoggingSerial")]
public class CrashReportTests
{
    static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "khaoz-crashreport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // One decoy report in the shape the writer generates: stem, stamp, process id, counter. A test that hand
    // writes a shorter name is testing a file the prune is entitled to ignore.
    static string Report(string prefix, string day, int sequence)
        => $"{prefix}-{day}-000000-000-{Environment.ProcessId}-{sequence}.log";

    static Exception Thrown(string message)
    {
        try { throw new InvalidOperationException(message); }
        catch (InvalidOperationException ex) { return ex; }   // caught, so it carries a real stack trace
    }

    [Fact]
    public void Format_CarriesTheIdentityOfTheCrash()
    {
        CrashReport.ClearNotes();
        Exception ex = Thrown("shader warming fell over");
        var when = new DateTimeOffset(2026, 8, 11, 21, 9, 1, TimeSpan.Zero);

        string report = CrashReport.Format("KhaozEngine Showcase", "Unhandled exception (terminating)", ex, null,
            when);

        Assert.Contains("timestamp: 2026-08-11T21:09:01", report, StringComparison.Ordinal);
        Assert.Contains("process: KhaozEngine Showcase", report, StringComparison.Ordinal);
        Assert.Contains("context: Unhandled exception (terminating)", report, StringComparison.Ordinal);
        Assert.Contains("exception: System.InvalidOperationException", report, StringComparison.Ordinal);
        Assert.Contains("message: shader warming fell over", report, StringComparison.Ordinal);
        Assert.Contains("--- stack ---", report, StringComparison.Ordinal);
        Assert.Contains(nameof(Thrown), report, StringComparison.Ordinal);   // the stack, not just the message
    }

    /// <summary>The engine version is the fact that says WHICH build crashed, and it is read off this assembly
    /// rather than passed in, so a head cannot report a version it is not running.</summary>
    [Fact]
    public void Format_NamesTheEngineVersionAndTheRuntime()
    {
        CrashReport.ClearNotes();
        string report = CrashReport.Format("head", "Unhandled exception", Thrown("x"), null,
            DateTimeOffset.UtcNow);

        string engine = typeof(CrashReport).Assembly.GetName().Version!.ToString(3);
        Assert.Contains("engine: " + engine, report, StringComparison.Ordinal);
        Assert.Contains("runtime: ", report, StringComparison.Ordinal);
        Assert.Contains("os: ", report, StringComparison.Ordinal);
    }

    /// <summary>The backend is the note GameApp pushes, and the reason notes exist at all: the package that
    /// owns the crash file cannot see the GPU.</summary>
    [Fact]
    public void Format_CarriesTheNotes_AndANoteReplacesItsPreviousValue()
    {
        CrashReport.ClearNotes();
        try
        {
            CrashReport.Note("backend", "Metal");
            CrashReport.Note("backend", "MetalNative");
            CrashReport.Note("room", "Boot");

            string report = CrashReport.Format("head", "Unhandled exception", Thrown("x"), null,
                DateTimeOffset.UtcNow);

            Assert.Contains("backend: MetalNative", report, StringComparison.Ordinal);
            Assert.DoesNotContain("backend: Metal\n", report, StringComparison.Ordinal);
            Assert.Contains("room: Boot", report, StringComparison.Ordinal);
        }
        finally { CrashReport.ClearNotes(); }
    }

    [Fact]
    public void Note_WithNoValue_DropsIt()
    {
        CrashReport.ClearNotes();
        try
        {
            CrashReport.Note("room", "Boot");
            CrashReport.Note("room", null);

            string report = CrashReport.Format("head", "Unhandled exception", Thrown("x"), null,
                DateTimeOffset.UtcNow);

            Assert.DoesNotContain("room:", report, StringComparison.Ordinal);
        }
        finally { CrashReport.ClearNotes(); }
    }

    /// <summary>A message with newlines in it would otherwise turn one record into several, and the header is
    /// read line by line.</summary>
    [Fact]
    public void Format_FlattensAMultiLineMessageOntoOneLine()
    {
        CrashReport.ClearNotes();
        string report = CrashReport.Format("head", "Unhandled exception",
            Thrown("first line\nsecond line"), null, DateTimeOffset.UtcNow);

        Assert.Contains("message: first line second line", report, StringComparison.Ordinal);
    }

    /// <summary>A thrown object that is not an <see cref="Exception"/> at all is rare and is exactly when the
    /// file matters, so it still produces a report rather than nothing.</summary>
    [Fact]
    public void Format_WithoutAnException_StillCarriesTheRawObject()
    {
        CrashReport.ClearNotes();
        string report = CrashReport.Format("head", "Unhandled exception", null, "weird-non-exception",
            DateTimeOffset.UtcNow);

        Assert.Contains("exception: System.String", report, StringComparison.Ordinal);
        Assert.Contains("weird-non-exception", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_CreatesOneFileNamedForTheProcess_AndReturnsIt()
    {
        CrashReport.ClearNotes();
        string dir = TempDir();
        try
        {
            string? path = CrashReport.Write(
                new CrashReportOptions { Directory = dir, ProcessLabel = "KhaozEngine Showcase" },
                "Unhandled exception (terminating)", Thrown("kaboom"), null);

            Assert.NotNull(path);
            Assert.True(File.Exists(path));
            Assert.StartsWith("KhaozEngine-Showcase-crash-", Path.GetFileName(path), StringComparison.Ordinal);
            Assert.EndsWith(".log", path, StringComparison.Ordinal);

            string body = File.ReadAllText(path!);
            Assert.Contains("KhaozEngine crash report", body, StringComparison.Ordinal);
            Assert.Contains("message: kaboom", body, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>The directory a game head writes into may not exist yet on a first crash.</summary>
    [Fact]
    public void Write_CreatesTheDirectory()
    {
        CrashReport.ClearNotes();
        string root = TempDir();
        string dir = Path.Combine(root, "nested", "crash");
        try
        {
            string? path = CrashReport.Write(new CrashReportOptions { Directory = dir, ProcessLabel = "head" },
                "Unhandled exception", Thrown("x"), null);

            Assert.NotNull(path);
            Assert.True(File.Exists(path));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>An unobserved-task-exception storm must not fill a player's disk, so the directory is bounded
    /// the same way the session logs are.</summary>
    [Fact]
    public void Write_KeepsAtMostTheRetainedCount()
    {
        CrashReport.ClearNotes();
        string dir = TempDir();
        try
        {
            // Eight earlier reports with distinct write times, in the shape the writer generates.
            var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < 8; i++)
            {
                string p = Path.Combine(dir, Report("head-crash", $"2026010{i}", i));
                File.WriteAllText(p, "x");
                File.SetLastWriteTimeUtc(p, baseTime.AddMinutes(i));   // i = 7 is newest
            }

            CrashReport.Write(
                new CrashReportOptions { Directory = dir, ProcessLabel = "head", MaxRetainedReports = 3 },
                "Unhandled exception", Thrown("x"), null);

            string[] remaining = Directory.GetFiles(dir, "head-crash-*.log")
                .Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;
            Assert.Equal(3, remaining.Length);
            Assert.Contains(Report("head-crash", "20260106", 6), remaining, StringComparer.Ordinal);
            Assert.Contains(Report("head-crash", "20260107", 7), remaining, StringComparer.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// RETENTION IS PER LABEL, AND THE DECOYS HERE ARE THE FILES THE OLD GLOB ACTUALLY ATE. Its predecessor
    /// stood up <c>other-crash-NN.log</c> against the pattern <c>head-crash-*.log</c>, which is disjoint by
    /// inspection: it could not have failed whatever the writer did. The real collision is a STEM that is a
    /// PREFIX of another label's stem. Label <c>show</c> globbed <c>show-crash-*.log</c> and that matched
    /// every report the label <c>show-crash</c> wrote, so one head's retention deleted another head's reports
    /// out of the shared OS crash directory.
    /// </summary>
    [Fact]
    public void Write_DoesNotPruneALabelWhoseStemStartsWithItsOwn()
    {
        CrashReport.ClearNotes();
        string dir = TempDir();
        try
        {
            // Written by the process label "show-crash", whose stem begins with the stem of the label "show".
            for (int i = 0; i < 4; i++)
                File.WriteAllText(Path.Combine(dir, Report("show-crash-crash", "20260101", i)), "x");

            CrashReport.Write(
                new CrashReportOptions { Directory = dir, ProcessLabel = "show", MaxRetainedReports = 1 },
                "Unhandled exception", Thrown("x"), null);

            Assert.Equal(4, Directory.GetFiles(dir, "show-crash-crash-*.log").Length);
            Assert.Single(Directory.GetFiles(dir, "show-crash-2*.log"));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// THE ONE COLLISION THAT REMAINS, PINNED SO IT STAYS DELIBERATE. Sanitisation is not injective: a space
    /// becomes a hyphen, so <c>My Game</c> and <c>My-Game</c> produce the same stem and therefore share one
    /// retention pool. No file-name rule can separate them, because there is no file name left to tell them
    /// apart, so the answer is the one the option documents: choose labels that do not sanitise onto each
    /// other.
    /// </summary>
    [Fact]
    public void Write_SharesARetentionPoolWithALabelThatSanitisesIdentically()
    {
        CrashReport.ClearNotes();
        string dir = TempDir();
        try
        {
            Assert.Equal(CrashReport.FileNamePrefix("My Game"), CrashReport.FileNamePrefix("My-Game"));

            for (int i = 0; i < 4; i++)
                File.WriteAllText(Path.Combine(dir, Report("My-Game-crash", "20260101", i)), "x");

            CrashReport.Write(
                new CrashReportOptions { Directory = dir, ProcessLabel = "My Game", MaxRetainedReports = 1 },
                "Unhandled exception", Thrown("x"), null);

            Assert.Single(Directory.GetFiles(dir, "My-Game-crash-*.log"));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// THE BURST, WHICH IS THE SHAPE THAT LOSES REPORTS. Unobserved task exceptions arrive from the finalizer
    /// thread back to back, and the file name used to end at the millisecond: twelve sequential writes
    /// measured seven files, each collision silently overwriting a sibling. The process id and the counter
    /// make the name unique by construction, so twelve writes are twelve files.
    /// </summary>
    [Fact]
    public void Write_TwelveInABurst_LeavesTwelveFiles()
    {
        CrashReport.ClearNotes();
        string dir = TempDir();
        try
        {
            var options = new CrashReportOptions
            {
                Directory = dir,
                ProcessLabel = "head",
                MaxRetainedReports = 100,
            };

            var paths = new List<string>();
            for (int i = 0; i < 12; i++)
                paths.Add(CrashReport.Write(options, "Unobserved task exception", Thrown("burst " + i), null)!);

            Assert.Equal(12, paths.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(12, Directory.GetFiles(dir, "head-crash-*.log").Length);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// AN UNOBSERVED TASK FAULT IS NOT THE CRASH FILE. It arrives from the finalizer thread at a collection,
    /// often while the game is still running fine, so it lands under its own stem: a tester asked for "the
    /// crash file" must not be handed a task fault from ten minutes earlier.
    /// </summary>
    [Fact]
    public void Write_AnUnobservedTaskFault_LandsUnderItsOwnStem()
    {
        CrashReport.ClearNotes();
        string dir = TempDir();
        try
        {
            string? path = CrashReport.Write(new CrashReportOptions { Directory = dir, ProcessLabel = "head" },
                "Unobserved task exception", Thrown("nobody awaited this"), null,
                CrashReportKind.UnobservedTask);

            Assert.NotNull(path);
            Assert.StartsWith("head-taskfault-", Path.GetFileName(path), StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(dir, "head-crash-*.log"));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// AND THE TWO POOLS ARE SEPARATE, which is the reason the stems are. A fault storm bounded to the same
    /// pool as the crashes would evict exactly the report worth keeping.
    /// </summary>
    [Fact]
    public void Write_CrashRetention_DoesNotTouchTheTaskFaultPool()
    {
        CrashReport.ClearNotes();
        string dir = TempDir();
        try
        {
            for (int i = 0; i < 4; i++)
                File.WriteAllText(Path.Combine(dir, Report("head-taskfault", "20260101", i)), "x");

            CrashReport.Write(
                new CrashReportOptions { Directory = dir, ProcessLabel = "head", MaxRetainedReports = 1 },
                "Unhandled exception", Thrown("x"), null);

            Assert.Equal(4, Directory.GetFiles(dir, "head-taskfault-*.log").Length);
            Assert.Single(Directory.GetFiles(dir, "head-crash-*.log"));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>The armed unobserved-task hook is what picks the stem in the field, not the caller.</summary>
    [Fact]
    public void OnCrash_ForAnUnobservedTaskException_WritesUnderTheTaskFaultStem()
    {
        CrashReport.ClearNotes();
        string dir = TempDir();
        try
        {
            CrashReport.Install(new CrashReportOptions { Directory = dir, ProcessLabel = "head" });
            string? path = CrashReport.OnCrash("Unobserved task exception", Thrown("faulted"), null,
                CrashReportKind.UnobservedTask);

            Assert.NotNull(path);
            Assert.StartsWith("head-taskfault-", Path.GetFileName(path), StringComparison.Ordinal);
        }
        finally
        {
            CrashReport.Uninstall();
            Directory.Delete(dir, true);
        }
    }

    /// <summary>The generated name is what the prune matches, and both halves of that are worth naming.</summary>
    [Fact]
    public void FileName_CarriesTheProcessAndTheCounter_AndIsWhatTheReportMatcherAccepts()
    {
        var when = new DateTimeOffset(2026, 8, 12, 3, 4, 5, 678, TimeSpan.Zero);
        string name = CrashReport.FileName("head-crash", when, 7);

        Assert.Equal($"head-crash-20260812-030405-678-{Environment.ProcessId}-7.log", name);
        Assert.True(CrashReport.IsReportName("head-crash", name));

        // The stem alone is not enough, which is the whole point of matching the shape.
        Assert.False(CrashReport.IsReportName("show-crash", CrashReport.FileName("show-crash-crash", when, 7)));
        Assert.False(CrashReport.IsReportName("head-crash", "head-crash-notes.log"));
        Assert.False(CrashReport.IsReportName("head-crash", "head-crash-20260812-030405-678.log"));
    }

    /// <summary>
    /// THE HOSTILE EXCEPTION, which is the shape this whole writer exists for. <c>Message</c> and
    /// <c>ToString</c> are both virtual, and an override that throws used to take the ENTIRE report with it:
    /// the render was one argument to the write call, so a throwing getter meant no file at all, and the prune
    /// had already deleted the oldest report on the way in. The floor is that the file exists and names the
    /// TYPE, which is where an investigation starts.
    /// </summary>
    [Fact]
    public void Write_WithAnExceptionWhoseMessageAndToStringBothThrow_StillNamesTheType()
    {
        CrashReport.ClearNotes();
        string dir = TempDir();
        try
        {
            string? path = CrashReport.Write(new CrashReportOptions { Directory = dir, ProcessLabel = "head" },
                "Unhandled exception (terminating)", new HostileException(), null);

            Assert.NotNull(path);
            string body = File.ReadAllText(path!);
            Assert.Contains("exception: " + typeof(HostileException).FullName, body, StringComparison.Ordinal);
            Assert.Contains("context: Unhandled exception (terminating)", body, StringComparison.Ordinal);
            Assert.Contains("--- stack ---", body, StringComparison.Ordinal);
            // Both halves say what happened rather than going missing.
            Assert.Contains("Message threw", body, StringComparison.Ordinal);
            Assert.Contains("ToString threw", body, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// AND A WRITE THAT FAILS DELETES NOTHING. The prune used to run first, so a crash that could not be
    /// written still cost the oldest report that could: the net effect of one hostile crash was minus one
    /// report and plus none. The failure is staged on the exact path the writer is about to open, which is
    /// what the clock-in seam is for.
    /// </summary>
    [Fact]
    public void Write_WhenTheFileCannotBeWritten_PrunesNothing()
    {
        CrashReport.ClearNotes();
        string dir = TempDir();
        try
        {
            var when = new DateTimeOffset(2026, 8, 12, 3, 4, 5, TimeSpan.Zero);
            // A DIRECTORY where the report's own file goes: writing to it fails on every platform.
            Directory.CreateDirectory(Path.Combine(dir, CrashReport.FileName("head-crash", when, 1)));

            for (int i = 0; i < 4; i++)
                File.WriteAllText(Path.Combine(dir, Report("head-crash", $"2026080{i}", i)), "x");

            string? path = CrashReport.WriteAt(
                new CrashReportOptions { Directory = dir, ProcessLabel = "head", MaxRetainedReports = 1 },
                "Unhandled exception", Thrown("x"), null, CrashReportKind.Unhandled, when, 1);

            Assert.Null(path);
            Assert.Equal(4, Directory.GetFiles(dir, "head-crash-2026080?-*.log").Length);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>It runs on the runtime's crash path, where a throw would replace the crash being reported with
    /// a different one.</summary>
    [Fact]
    public void Write_NeverThrows_WhenTheDirectoryCannotBeUsed()
    {
        CrashReport.ClearNotes();
        string dir = TempDir();
        try
        {
            // A FILE where the directory should be: CreateDirectory cannot make one here.
            string blocked = Path.Combine(dir, "blocked");
            File.WriteAllText(blocked, "x");

            string? path = null;
            Exception? thrown = Record.Exception(() => path = CrashReport.Write(
                new CrashReportOptions { Directory = blocked, ProcessLabel = "head" },
                "Unhandled exception", Thrown("x"), null));

            Assert.Null(thrown);
            Assert.Null(path);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>The armed path, driven through the seam the installed handlers call, because an actual
    /// <see cref="AppDomain.UnhandledException"/> takes the test process with it.</summary>
    [Fact]
    public void OnCrash_WritesWhileArmed_AndNothingOnceUninstalled()
    {
        CrashReport.ClearNotes();
        string dir = TempDir();
        try
        {
            CrashReport.Install(new CrashReportOptions { Directory = dir, ProcessLabel = "head" });
            string? path = CrashReport.OnCrash("Unhandled exception (terminating)", Thrown("armed"), null);

            Assert.NotNull(path);
            Assert.Contains("message: armed", File.ReadAllText(path!), StringComparison.Ordinal);

            CrashReport.Uninstall();
            Assert.Null(CrashReport.OnCrash("Unhandled exception", Thrown("disarmed"), null));
            Assert.Single(Directory.GetFiles(dir, "head-crash-*.log"));
        }
        finally
        {
            CrashReport.Uninstall();
            Directory.Delete(dir, true);
        }
    }

    /// <summary>A second arming replaces the first rather than stacking a second handler onto the same
    /// process, which is what would double-write every crash.</summary>
    [Fact]
    public void Install_IsIdempotent()
    {
        CrashReport.ClearNotes();
        string first = TempDir();
        string second = TempDir();
        try
        {
            CrashReport.Install(new CrashReportOptions { Directory = first, ProcessLabel = "head" });
            CrashReport.Install(new CrashReportOptions { Directory = second, ProcessLabel = "head" });

            CrashReport.OnCrash("Unhandled exception", Thrown("x"), null);

            Assert.Empty(Directory.GetFiles(first, "head-crash-*.log"));
            Assert.Single(Directory.GetFiles(second, "head-crash-*.log"));
        }
        finally
        {
            CrashReport.Uninstall();
            Directory.Delete(first, true);
            Directory.Delete(second, true);
        }
    }

    /// <summary>The default location is the point of the whole thing: the file has to land where whoever
    /// collects the operating system's own crash report will see it.</summary>
    [Theory]
    [InlineData(true, false, "/Users/tester", null, "/Users/tester/Library/Logs/KhaozEngine")]
    [InlineData(false, true, @"C:\Users\tester", null, @"C:\Users\tester\AppData\Local/KhaozEngine/crash")]
    [InlineData(false, false, "/home/tester", null, "/home/tester/.local/state/KhaozEngine/crash")]
    [InlineData(false, false, "/home/tester", "/home/tester/.state", "/home/tester/.state/KhaozEngine/crash")]
    public void ResolveDefaultDirectory_TakesThePlatformsCrashLocation(bool isMacOS, bool isWindows, string home,
        string? xdgStateHome, string expected)
    {
        string localAppData = isWindows ? Path.Combine(home, "AppData", "Local") : "/unused";

        string resolved = CrashReport.ResolveDefaultDirectory(isMacOS, isWindows, home, xdgStateHome,
            localAppData, tempDirectory: "/tmp");

        Assert.Equal(Normalize(expected), Normalize(resolved));
    }

    /// <summary>An environment that answers nothing still resolves to somewhere writable rather than
    /// throwing out of the arming call.</summary>
    [Fact]
    public void ResolveDefaultDirectory_FallsBackToTemp()
    {
        string resolved = CrashReport.ResolveDefaultDirectory(isMacOS: false, isWindows: false, home: null,
            xdgStateHome: null, localAppData: null, tempDirectory: "/tmp");

        Assert.Equal(Normalize("/tmp/KhaozEngine/crash"), Normalize(resolved));
    }

    /// <summary>A window title is what names the file, and a title is free text.</summary>
    [Theory]
    [InlineData("KhaozEngine Showcase", "KhaozEngine-Showcase-crash")]
    [InlineData("", "game-crash")]
    [InlineData(null, "game-crash")]
    public void FileNamePrefix_IsSafeForAFileName(string? label, string expected)
        => Assert.Equal(expected, CrashReport.FileNamePrefix(label));

    static string Normalize(string path) => path.Replace('\\', '/');

    /// <summary>
    /// An exception whose two virtual readers both throw. Hostile by construction here, and ordinary in the
    /// field: an override that reads state the crash already tore down behaves exactly the same way.
    /// </summary>
    sealed class HostileException : Exception
    {
        public override string Message => throw new NotSupportedException("Message is hostile.");

        public override string ToString() => throw new NotSupportedException("ToString is hostile.");
    }
}
