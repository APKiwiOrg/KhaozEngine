using System;
using System.IO;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Logging;

[Collection("LoggingSerial")]   // redirects Console.Out/Error: must not run parallel to other classes
public class ConsoleSinkTests
{
    private static LogEntry Entry(string msg, LogLevel level) =>
        new(new DateTimeOffset(2026, 6, 10, 1, 2, 3, TimeSpan.Zero), level, "Cat", msg);

    [Fact]
    public void EmitWritesFormattedLineToStdout()
    {
        var originalOut = Console.Out;
        var buffer = new StringWriter();
        Console.SetOut(buffer);
        try
        {
            using var sink = new ConsoleSink();
            sink.Emit(Entry("hello", LogLevel.Info));
        }
        finally { Console.SetOut(originalOut); }

        Assert.Contains("[INFO] [Cat] hello", buffer.ToString());
    }

    [Fact]
    public void ErrorsGoToStdErrWhenEnabled()
    {
        var originalErr = Console.Error;
        var errBuffer = new StringWriter();
        Console.SetError(errBuffer);
        try
        {
            using var sink = new ConsoleSink(useStdErrForErrors: true);
            sink.Emit(Entry("boom", LogLevel.Error));
        }
        finally { Console.SetError(originalErr); }

        Assert.Contains("[ERROR] [Cat] boom", errBuffer.ToString());
    }

    [Fact]
    public void BelowSinkMinimumLevelIsSkipped()
    {
        var originalOut = Console.Out;
        var buffer = new StringWriter();
        Console.SetOut(buffer);
        try
        {
            using var sink = new ConsoleSink(minimumLevel: LogLevel.Warn);
            sink.Emit(Entry("verbose", LogLevel.Info));
        }
        finally { Console.SetOut(originalOut); }

        Assert.Equal(string.Empty, buffer.ToString().Trim());
    }
}
