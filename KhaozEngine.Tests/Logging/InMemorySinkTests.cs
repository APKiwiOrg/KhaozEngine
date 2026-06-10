using System;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Logging;

public class InMemorySinkTests
{
    private static LogEntry Entry(string msg, LogLevel level = LogLevel.Info) =>
        new(DateTimeOffset.UnixEpoch, level, "Test", msg);

    [Fact]
    public void EmitCapturesEntriesInOrder()
    {
        var sink = new InMemorySink();
        sink.Emit(Entry("a"));
        sink.Emit(Entry("b"));

        Assert.Collection(sink.Entries,
            e => Assert.Equal("a", e.Message),
            e => Assert.Equal("b", e.Message));
    }

    [Fact]
    public void EntriesIsASnapshotNotLiveView()
    {
        var sink = new InMemorySink();
        sink.Emit(Entry("a"));
        var snapshot = sink.Entries;
        sink.Emit(Entry("b"));
        Assert.Single(snapshot);
    }

    [Fact]
    public void ClearRemovesAllEntries()
    {
        var sink = new InMemorySink();
        sink.Emit(Entry("a"));
        sink.Clear();
        Assert.Empty(sink.Entries);
    }
}
