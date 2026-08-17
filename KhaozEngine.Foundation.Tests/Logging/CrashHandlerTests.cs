using System;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Logging;

/// <summary>
/// <see cref="CrashHandler"/>, both of its install shapes. The ambient one resolves the manager when the crash is
/// reported (#633), so it follows every <see cref="Log.Configure(LoggerOptions)"/> the process makes, before or
/// after the install. The injected one pins, on purpose, and the last case here is the guard on that.
///
/// <para>In <c>LoggingSerial</c> because every case writes process-global state: the handler's own arming and,
/// in the ambient cases, the <see cref="Log"/> facade. Each case builds its own managers and finishes by
/// shutting them down, never by re-adopting one it already shut down, since shutdown clears a manager's
/// sinks and a re-adopted manager is a silent no-op logger for the rest of the run.</para>
/// </summary>
[Collection("LoggingSerial")]   // CrashHandler holds process-global state: run serially (collection defined in Task 6)
public class CrashHandlerTests
{
    private static (LogManager mgr, InMemorySink sink) SyncManager()
    {
        var (options, sink) = SyncOptions();
        return (new LogManager(options), sink);
    }

    private static (LoggerOptions options, InMemorySink sink) SyncOptions()
    {
        var sink = new InMemorySink();
        var options = new LoggerOptions { Synchronous = true, MinimumLevel = LogLevel.Trace };
        options.Sinks.Add(sink);
        return (options, sink);
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

    [Fact]
    public void AmbientInstall_ReportsThroughTheManagerConfiguredWhenTheCrashHappens()
    {
        // The shipped SessionLog ordering (SessionLog.cs:62-63): configure, then install. The game later swaps
        // its sink set, which shuts the first manager down and clears its sinks, and the fatal line used to go
        // there and vanish while the live session log said nothing about the crash.
        var (firstOptions, firstSink) = SyncOptions();
        var (secondOptions, secondSink) = SyncOptions();
        try
        {
            Log.Configure(firstOptions);
            CrashHandler.Install();
            Log.Configure(secondOptions);

            CrashHandler.Report("Unhandled exception (terminating)", new InvalidOperationException("kaboom"), null);

            Assert.Empty(firstSink.Entries);
            var e = Assert.Single(secondSink.Entries);
            Assert.Equal(LogLevel.Fatal, e.Level);
            Assert.Equal("Crash", e.Category);
            Assert.Contains("Unhandled exception (terminating)", e.Message);
            Assert.Equal("kaboom", e.Exception!.Message);
        }
        finally { CrashHandler.Uninstall(); Log.Shutdown(); }
    }

    [Fact]
    public void AmbientInstall_BeforeAnyConfigure_ReportsOnceConfigurationLands()
    {
        Log.Shutdown();   // unconfigured, the state an install at the process entry point sees
        var (options, sink) = SyncOptions();
        try
        {
            CrashHandler.Install();   // used to no-op permanently, because Log.Manager was null here
            Log.Configure(options);

            CrashHandler.Report("Unhandled exception", new InvalidOperationException("late"), null);

            var e = Assert.Single(sink.Entries);
            Assert.Equal(LogLevel.Fatal, e.Level);
            Assert.Equal("Crash", e.Category);
            Assert.Equal("late", e.Exception!.Message);
        }
        finally { CrashHandler.Uninstall(); Log.Shutdown(); }
    }

    [Fact]
    public void AmbientInstall_WithNothingConfigured_DoesNotThrow()
    {
        Log.Shutdown();
        try
        {
            CrashHandler.Install();
            var ex = Record.Exception(
                () => CrashHandler.Report("Unhandled exception", new InvalidOperationException("nowhere"), null));
            Assert.Null(ex);
        }
        finally { CrashHandler.Uninstall(); }
    }

    [Fact]
    public void InjectedInstall_StaysPinnedAcrossAnAmbientReconfigure()
    {
        // The deliberate opposite of the ambient cases: a caller who hands in a manager gets that manager's
        // sinks, which is what makes an injected wiring and its test assertions mean what they say.
        var (mgr, ownSink) = SyncManager();
        var (ambientOptions, ambientSink) = SyncOptions();
        try
        {
            CrashHandler.Install(mgr);
            Log.Configure(ambientOptions);

            CrashHandler.Report("Unhandled exception", new InvalidOperationException("stays home"), null);

            var e = Assert.Single(ownSink.Entries);
            Assert.Equal("stays home", e.Exception!.Message);
            Assert.Empty(ambientSink.Entries);
        }
        finally { CrashHandler.Uninstall(); Log.Shutdown(); mgr.Shutdown(); }
    }

    [Fact]
    public void Uninstall_StopsReporting_EvenWhileTheAmbientLogIsLive()
    {
        // Resolving per report must not turn an uninstalled handler into an ambient writer.
        var (options, sink) = SyncOptions();
        try
        {
            Log.Configure(options);
            CrashHandler.Install();
            CrashHandler.Uninstall();

            CrashHandler.Report("after", new InvalidOperationException("x"), null);

            Assert.Empty(sink.Entries);
        }
        finally { CrashHandler.Uninstall(); Log.Shutdown(); }
    }

    [Fact]
    public void ReportAfterAsyncManagerShutdownDoesNotThrow()
    {
        var sink = new InMemorySink();
        var options = new LoggerOptions { Synchronous = false, MinimumLevel = LogLevel.Trace };
        options.Sinks.Add(sink);
        var mgr = new LogManager(options);
        try
        {
            CrashHandler.Install(mgr);
            mgr.Shutdown();   // async queue disposed; a later crash signal must not throw from the handler
            var ex = Record.Exception(() => CrashHandler.Report("Unhandled exception", new InvalidOperationException("late"), null));
            Assert.Null(ex);
        }
        finally { CrashHandler.Uninstall(); }
    }
}
