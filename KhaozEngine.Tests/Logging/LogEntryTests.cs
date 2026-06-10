// KhaozEngine.Tests/Logging/LogEntryTests.cs
using System;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Logging;

public class LogEntryTests
{
    [Fact]
    public void ConstructorStoresAllFields()
    {
        var ts = new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);
        var ex = new InvalidOperationException("x");
        var entry = new LogEntry(ts, LogLevel.Warn, "Boot", "started", ex);

        Assert.Equal(ts, entry.Timestamp);
        Assert.Equal(LogLevel.Warn, entry.Level);
        Assert.Equal("Boot", entry.Category);
        Assert.Equal("started", entry.Message);
        Assert.Same(ex, entry.Exception);
    }

    [Fact]
    public void ExceptionDefaultsToNull()
    {
        var entry = new LogEntry(DateTimeOffset.UnixEpoch, LogLevel.Info, "App", "hi");
        Assert.Null(entry.Exception);
    }

    [Fact]
    public void LevelsAreOrderedTraceLowToFatalHigh()
    {
        Assert.True(LogLevel.Trace < LogLevel.Debug);
        Assert.True(LogLevel.Debug < LogLevel.Info);
        Assert.True(LogLevel.Info < LogLevel.Warn);
        Assert.True(LogLevel.Warn < LogLevel.Error);
        Assert.True(LogLevel.Error < LogLevel.Fatal);
    }
}
