using System;
using System.Diagnostics;
using System.Text;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Logging;

[Collection("LoggingSerial")]   // adds a global Trace listener: must not run parallel to other classes
public class DebugSinkTests
{
    private sealed class CapturingListener : TraceListener
    {
        public readonly StringBuilder Output = new();
        public override void Write(string? message) => Output.Append(message);
        public override void WriteLine(string? message) => Output.Append(message).Append('\n');
    }

    [Fact]
    public void EmitWritesToTraceListeners()
    {
        var listener = new CapturingListener();
        Trace.Listeners.Add(listener);
        try
        {
            using var sink = new DebugSink();
            sink.Emit(new LogEntry(new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero), LogLevel.Info, "Cat", "trace me"));
        }
        finally { Trace.Listeners.Remove(listener); }

        Assert.Contains("[INFO] [Cat] trace me", listener.Output.ToString());
    }

    [Fact]
    public void BelowSinkMinimumLevelIsSkipped()
    {
        var listener = new CapturingListener();
        Trace.Listeners.Add(listener);
        try
        {
            using var sink = new DebugSink(minimumLevel: LogLevel.Error);
            sink.Emit(new LogEntry(DateTimeOffset.UnixEpoch, LogLevel.Info, "Cat", "skip"));
        }
        finally { Trace.Listeners.Remove(listener); }

        Assert.Equal(string.Empty, listener.Output.ToString());
    }
}
