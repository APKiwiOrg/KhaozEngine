using System;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Logging;

[Collection("LoggingSerial")]   // CrashHandler holds process-global state: run serially (collection defined in Task 6)
public class CrashHandlerTests
{
    private static (LogManager mgr, InMemorySink sink) SyncManager()
    {
        var sink = new InMemorySink();
        var options = new LoggerOptions { Synchronous = true, MinimumLevel = LogLevel.Trace };
        options.Sinks.Add(sink);
        return (new LogManager(options), sink);
    }

    [Fact]
    public void ReportLogsFatalCrashEntryWithException()
    {
        var (mgr, sink) = SyncManager();
        try
        {
            CrashHandler.Install(mgr);
            CrashHandler.Report("Unhandled exception", new InvalidOperationException("kaboom"), null);

            var e = Assert.Single(sink.Entries);
            Assert.Equal(LogLevel.Fatal, e.Level);
            Assert.Equal("Crash", e.Category);
            Assert.Contains("Unhandled exception", e.Message);
            Assert.NotNull(e.Exception);
            Assert.Equal("kaboom", e.Exception!.Message);
        }
        finally { CrashHandler.Uninstall(); }
    }

    [Fact]
    public void ReportWithoutExceptionStillLogsRawObject()
    {
        var (mgr, sink) = SyncManager();
        try
        {
            CrashHandler.Install(mgr);
            CrashHandler.Report("Unhandled exception object", null, "weird-non-exception");
            var e = Assert.Single(sink.Entries);
            Assert.Equal(LogLevel.Fatal, e.Level);
            Assert.Contains("weird-non-exception", e.Message);
        }
        finally { CrashHandler.Uninstall(); }
    }

    [Fact]
    public void ReportWithoutInstallIsNoOp()
    {
        CrashHandler.Uninstall();   // ensure detached
        var ex = Record.Exception(() => CrashHandler.Report("nobody", new Exception("x"), null));
        Assert.Null(ex);
    }

    [Fact]
    public void InstallTwiceThenUninstallLeavesNoHandlers()
    {
        var (mgr, _) = SyncManager();
        CrashHandler.Install(mgr);
        CrashHandler.Install(mgr);   // must not double-register
        CrashHandler.Uninstall();
        // After a single Uninstall the AppDomain handler is gone; Report is now a no-op.
        var ex = Record.Exception(() => CrashHandler.Report("after", new Exception("x"), null));
        Assert.Null(ex);
    }
}
