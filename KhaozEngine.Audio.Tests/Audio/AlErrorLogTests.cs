using KhaozEngine.Audio;
using KhaozEngine.Diagnostics;
using Silk.NET.OpenAL;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// The OpenAL error sweep (#113). Before it, nothing in the native-audio path ever read the AL error latch, so
/// a device that changed or went away mid-session made every later call a silent no-op and the game just went
/// quiet. These pin the reporting contract the backends now call into: clean codes stay silent, a real code is
/// logged, and a per-frame path that fails every frame logs once instead of flooding the log.
/// </summary>
public sealed class AlErrorLogTests
{
    [Fact]
    public void NoError_LogsNothingAndCountsNothing()
    {
        var (log, sink) = NewLog();

        Assert.False(log.Check("SFX SourcePlay", AudioError.NoError));

        Assert.Empty(sink.Entries);
        Assert.Equal(0, log.ErrorCount);
    }

    [Fact]
    public void Error_IsLoggedOncePerOperationButAlwaysCounted()
    {
        var (log, sink) = NewLog();

        // A dead device fails the same per-frame call every frame. Only the first one is worth a log line.
        for (int frame = 0; frame < 5; frame++)
            Assert.True(log.Check("music buffer refill", AudioError.OutOfMemory));

        var entry = Assert.Single(sink.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("music buffer refill", entry.Message);
        Assert.Contains("OutOfMemory", entry.Message);
        Assert.Equal(5, log.ErrorCount);
    }

    [Fact]
    public void EachOperationGetsItsOwnLine()
    {
        var (log, sink) = NewLog();

        log.Check("music buffer refill", AudioError.InvalidValue);
        log.Check("SFX BufferData", AudioError.OutOfMemory);
        log.Check("music buffer refill", AudioError.InvalidValue);

        Assert.Equal(2, sink.Entries.Count);
        Assert.Contains(sink.Entries, e => e.Message.Contains("SFX BufferData"));
        Assert.Equal(3, log.ErrorCount);
    }

    [Fact]
    public void ContextErrorEnumIsAccepted()
    {
        // The context half of the sweep reports alcGetError, a different enum from alGetError's. Both have a
        // zero "no error" member, which is the whole contract the generic check leans on.
        var (log, sink) = NewLog();

        Assert.False(log.Check("context setup", ContextError.NoError));
        Assert.True(log.Check("context setup", ContextError.InvalidDevice));

        Assert.Single(sink.Entries);
    }

    // A private LogManager (synchronous, so entries land inline) writing into an InMemorySink. Touches no
    // process-global logging state, so this class needs no serial collection.
    static (AlErrorLog Log, InMemorySink Sink) NewLog()
    {
        var sink = new InMemorySink();
        var options = new LoggerOptions { Synchronous = true, MinimumLevel = LogLevel.Trace };
        options.Sinks.Add(sink);
        return (new AlErrorLog(new LogManager(options).GetLogger("test")), sink);
    }
}
