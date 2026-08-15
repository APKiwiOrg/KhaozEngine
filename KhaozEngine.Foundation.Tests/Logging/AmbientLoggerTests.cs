using System;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Logging;

/// <summary>
/// #616: a logger obtained from the ambient <see cref="Log"/> facade must follow the facade, not the manager that
/// happened to be configured when it was resolved.
///
/// <para><b>THE FAILURE THIS PINS.</b> 23 types across the GPU packages cache their category logger in a
/// <c>static readonly ILogger</c> field, resolved at type initialization. Before the fix, that field held whatever
/// the facade had at that instant, and <see cref="Log.Configure(LoggerOptions)"/> SHUTS DOWN the manager it
/// replaces (which disposes and clears that manager's sinks). So a consumer that touched any of those types before
/// configuring logging, or that reconfigured afterwards, got a logger that reported itself enabled, kept
/// submitting, and dropped every entry for the life of the process. No exception, no warning, nothing in the log
/// to notice missing.</para>
///
/// <para><b>WHY THE TESTS RESOLVE INTO A LOCAL AND NOT A STATIC FIELD.</b> A static field would pin the logger to
/// whichever test ran first in the assembly, which is the bug rather than a test of it. A local resolved before
/// the reconfigure reproduces the same capture with the lifetime under the test's control.</para>
///
/// <para>In <c>LoggingSerial</c> because every case writes the process-global facade. That collection's
/// <c>DisableParallelization</c> is also what makes the allocation case below sound: no other collection is
/// allocating on another thread while its window is open.</para>
/// </summary>
[Collection("LoggingSerial")]
public class AmbientLoggerTests
{
    private static (LoggerOptions options, InMemorySink sink) SyncOptions(
        LogLevel minimum = LogLevel.Trace, string defaultCategory = "App")
    {
        var sink = new InMemorySink();
        var options = new LoggerOptions { Synchronous = true, MinimumLevel = minimum, DefaultCategory = defaultCategory };
        options.Sinks.Add(sink);
        return (options, sink);
    }

    [Fact]
    public void LoggerResolvedBeforeConfigure_WritesOnceConfigurationLands()
    {
        Log.Shutdown();   // ensure unconfigured, the state a type initializer at process start sees
        var captured = Log.For<AmbientLoggerTests>();   // the static-field capture, in a local

        var (options, sink) = SyncOptions();
        try
        {
            Log.Configure(options);
            captured.Info("late configuration");

            var e = Assert.Single(sink.Entries);
            Assert.Equal(nameof(AmbientLoggerTests), e.Category);
            Assert.Equal("late configuration", e.Message);
        }
        finally { Log.Shutdown(); }
    }

    [Fact]
    public void LoggerResolvedBeforeReconfigure_FollowsTheNewManager()
    {
        var (firstOptions, firstSink) = SyncOptions();
        var (secondOptions, secondSink) = SyncOptions();
        try
        {
            Log.Configure(firstOptions);
            var captured = Log.For<AmbientLoggerTests>();   // resolved against the FIRST manager
            captured.Info("to the first");
            Assert.Single(firstSink.Entries);

            // Replaces and shuts the first down, which disposes and clears its sinks. A pinned logger would keep
            // submitting into that gutted manager from here on, silently.
            Log.Configure(secondOptions);
            captured.Info("to the second");

            Assert.Single(firstSink.Entries);   // still just the pre-reconfigure line
            var e = Assert.Single(secondSink.Entries);
            Assert.Equal(nameof(AmbientLoggerTests), e.Category);
            Assert.Equal("to the second", e.Message);
        }
        finally { Log.Shutdown(); }
    }

    [Fact]
    public void GetLoggerResolvedBeforeReconfigure_FollowsTheNewManager()
    {
        // The Log.Get shape, which MetalCompletionHandler uses because a static class cannot be a type argument.
        var (firstOptions, _) = SyncOptions();
        var (secondOptions, secondSink) = SyncOptions();
        try
        {
            Log.Configure(firstOptions);
            var captured = Log.Get("MetalCompletionHandler");

            Log.Configure(secondOptions);
            captured.Warn("uncommitted");

            var e = Assert.Single(secondSink.Entries);
            Assert.Equal("MetalCompletionHandler", e.Category);
            Assert.Equal(LogLevel.Warn, e.Level);
        }
        finally { Log.Shutdown(); }
    }

    [Fact]
    public void ConvenienceMethods_FollowAReconfiguredDefaultCategory()
    {
        var (firstOptions, _) = SyncOptions(defaultCategory: "Boot");
        var (secondOptions, secondSink) = SyncOptions(defaultCategory: "Game");
        try
        {
            Log.Configure(firstOptions);
            Log.Configure(secondOptions);
            Log.Warn("careful");

            var e = Assert.Single(secondSink.Entries);
            Assert.Equal("Game", e.Category);
        }
        finally { Log.Shutdown(); }
    }

    [Fact]
    public void InjectedManagerLogger_StaysBoundToItsManager()
    {
        // The deliberate opposite of the cases above, and the reason the fix is at the facade rather than inside
        // CategoryLogger: a logger taken from a manager the caller owns must keep writing to THAT manager, or
        // every test and DI wiring that asserts against its own sink stops meaning anything.
        var (ownOptions, ownSink) = SyncOptions();
        var (ambientOptions, ambientSink) = SyncOptions();
        var own = new LogManager(ownOptions);
        try
        {
            Log.Configure(ambientOptions);
            own.GetLogger("Owned").Info("stays home");

            Assert.Single(ownSink.Entries);
            Assert.Empty(ambientSink.Entries);
        }
        finally { own.Shutdown(); Log.Shutdown(); }
    }

    [Fact]
    public void FilteredCallThroughACachedLogger_AllocatesNothing()
    {
        // The per-message cost of the ambient binding is one volatile read of the configured manager, and this is
        // the assertion that keeps it there. Logging BELOW the minimum level isolates that binding: the call
        // resolves the manager, asks it whether the level passes, and returns, so anything the write path itself
        // allocates (the sink-array snapshot, the formatter) is out of the window.
        const string message = "filtered";
        var (options, sink) = SyncOptions(minimum: LogLevel.Error);
        try
        {
            Log.Configure(options);
            var captured = Log.For<AmbientLoggerTests>();

            Loop(captured);                      // JIT the whole path before measuring it
            Assert.Empty(sink.Entries);          // and confirm the level filter really did swallow them

            NoAllocation("a filtered call through a cached ambient logger", () => Loop(captured));
            Assert.Empty(sink.Entries);
        }
        finally { Log.Shutdown(); }

        static void Loop(ILogger logger)
        {
            for (int i = 0; i < 2000; i++) logger.Debug(message);
        }
    }

    /// <summary>
    /// Measures bytes allocated on this thread around <paramref name="loop"/> and asserts zero, retrying once.
    /// Same shape and same reason as <c>KhaozEngine.Tests.AllocAssert</c> in the Render tests, which cannot be
    /// referenced from here: a gen-0 collection provoked elsewhere in the process can land inside the measurement
    /// window and bill this thread for foreign bytes, while a genuine per-call allocation fails both passes.
    /// </summary>
    private static void NoAllocation(string description, Action loop)
    {
        long first = Measure(loop);
        if (first == 0) return;

        long retry = Measure(loop);
        Assert.True(retry == 0,
            $"{description} allocated {first} bytes on the first pass and {retry} on the retry, expected zero on at least one");

        static long Measure(Action loop)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            loop();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }
}
