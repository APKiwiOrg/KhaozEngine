using System;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Logging;

[Collection("LoggingSerial")]   // static Log state: run serially, never parallel to other classes (collection defined in Task 6)
public class LogFacadeTests
{
    private static (LoggerOptions options, InMemorySink sink) SyncOptions()
    {
        var sink = new InMemorySink();
        var options = new LoggerOptions { Synchronous = true, MinimumLevel = LogLevel.Trace, DefaultCategory = "App" };
        options.Sinks.Add(sink);
        return (options, sink);
    }

    [Fact]
    public void CallsBeforeConfigureAreNoOps()
    {
        Log.Shutdown();   // ensure unconfigured
        Assert.False(Log.IsConfigured);
        var ex = Record.Exception(() =>
        {
            Log.Info("nobody home");
            Log.For<LogFacadeTests>().Error("still nobody");
        });
        Assert.Null(ex);
        Assert.NotNull(Log.For<LogFacadeTests>());   // returns a no-op logger, never null
    }

    [Fact]
    public void ConfigureRoutesToManager()
    {
        var (options, sink) = SyncOptions();
        try
        {
            Log.Configure(options);
            Assert.True(Log.IsConfigured);
            Log.For<LogFacadeTests>().Info("routed");
            var e = Assert.Single(sink.Entries);
            Assert.Equal(nameof(LogFacadeTests), e.Category);
            Assert.Equal("routed", e.Message);
        }
        finally { Log.Shutdown(); }
    }

    [Fact]
    public void ConvenienceMethodsUseDefaultCategory()
    {
        var (options, sink) = SyncOptions();
        try
        {
            Log.Configure(options);
            Log.Warn("careful");
            var e = Assert.Single(sink.Entries);
            Assert.Equal("App", e.Category);
            Assert.Equal(LogLevel.Warn, e.Level);
        }
        finally { Log.Shutdown(); }
    }

    [Fact]
    public void MinimumLevelDelegatesToManager()
    {
        var (options, _) = SyncOptions();
        try
        {
            Log.Configure(options);
            Log.MinimumLevel = LogLevel.Error;
            Assert.Equal(LogLevel.Error, Log.MinimumLevel);
        }
        finally { Log.Shutdown(); }
    }

    [Fact]
    public void ReconfiguringShutsDownThePreviousManager()
    {
        var firstSink = new InMemorySink();
        var firstOptions = new LoggerOptions { Synchronous = true };
        firstOptions.Sinks.Add(firstSink);
        var (secondOptions, secondSink) = SyncOptions();
        try
        {
            Log.Configure(firstOptions);
            Log.Configure(secondOptions);   // replaces + shuts down the first
            Log.Get("X").Info("to second");

            Assert.Empty(firstSink.Entries);
            Assert.Single(secondSink.Entries);
        }
        finally { Log.Shutdown(); }
    }

    [Fact]
    public void ShutdownDetachesManager()
    {
        var (options, _) = SyncOptions();
        Log.Configure(options);
        Log.Shutdown();
        Assert.False(Log.IsConfigured);
        Assert.Null(Log.Manager);
    }
}
