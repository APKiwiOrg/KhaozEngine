using System;
using System.Linq;
using System.Threading;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Logging;

public class RecentLogSinkTests
{
    private const string TruncationMarker = " [truncated]";

    private static LogEntry Entry(string msg) =>
        new(DateTimeOffset.UnixEpoch, LogLevel.Info, "Test", msg);

    [Fact]
    public void Keeps_the_newest_capacity_lines_oldest_first()
    {
        var sink = new RecentLogSink(capacity: 3);
        for (int i = 1; i <= 5; i++)
        {
            sink.Emit(Entry("m" + i));
        }

        var lines = sink.Snapshot();

        Assert.Equal(3, lines.Count);
        Assert.Collection(lines,
            l => Assert.EndsWith("m3", l),
            l => Assert.EndsWith("m4", l),
            l => Assert.EndsWith("m5", l));
    }

    [Fact]
    public void Emit_survives_an_exception_whose_ToString_throws()
    {
        var sink = new RecentLogSink();

        sink.Emit(new LogEntry(DateTimeOffset.UnixEpoch, LogLevel.Error, "Test", "bad", new UnprintableException()));
        sink.Emit(Entry("after"));

        Assert.EndsWith("after", Assert.Single(sink.Snapshot()));
    }

    [Fact]
    public void Emit_keeps_entries_with_a_null_message()
    {
        var sink = new RecentLogSink();

        sink.Emit(default);
        sink.Emit(new LogEntry(DateTimeOffset.UnixEpoch, LogLevel.Info, null!, null!));

        Assert.Equal(2, sink.Snapshot().Count);
        Assert.All(sink.Snapshot(), line => Assert.EndsWith("] ", line));
    }

    [Fact]
    public void Lines_use_the_log_formatter()
    {
        var sink = new RecentLogSink();
        var entry = new LogEntry(DateTimeOffset.UnixEpoch, LogLevel.Warn, "Net", "lost packet", new InvalidOperationException("boom"));

        sink.Emit(entry);

        Assert.Equal(LogFormatter.Format(entry), sink.Snapshot()[0]);
    }

    [Fact]
    public void Long_entry_is_cut_with_a_marker()
    {
        var sink = new RecentLogSink();

        sink.Emit(Entry(new string('x', 5000)));

        string line = sink.Snapshot()[0];
        Assert.Equal(RecentLogSink.MaxEntryChars + TruncationMarker.Length, line.Length);
        Assert.EndsWith(TruncationMarker, line);
    }

    [Fact]
    public void Cut_never_splits_a_surrogate_pair()
    {
        var sink = new RecentLogSink();
        string prefix = LogFormatter.Format(Entry(string.Empty));
        // Place a pair so its high half is the last character the cut would keep.
        string message = new string('x', RecentLogSink.MaxEntryChars - 1 - prefix.Length) + "\U0001F600" + new string('y', 100);

        sink.Emit(Entry(message));

        string line = sink.Snapshot()[0];
        Assert.EndsWith(TruncationMarker, line);
        string kept = line[..^TruncationMarker.Length];
        Assert.Equal(RecentLogSink.MaxEntryChars - 1, kept.Length);
        Assert.False(char.IsHighSurrogate(kept[^1]));
    }

    [Fact]
    public void Concurrent_emits_stay_bounded()
    {
        var sink = new RecentLogSink(capacity: 500);
        var threads = Enumerable.Range(0, 4)
            .Select(t => new Thread(() =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    sink.Emit(Entry($"t{t} m{i}"));
                }
            }))
            .ToArray();

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        Assert.Equal(500, sink.Snapshot().Count);
    }

    [Fact]
    public void Snapshot_is_a_copy()
    {
        var sink = new RecentLogSink(capacity: 2);
        sink.Emit(Entry("a"));
        var earlier = sink.Snapshot();

        sink.Emit(Entry("b"));
        sink.Emit(Entry("c"));

        Assert.Single(earlier);
        Assert.EndsWith("a", earlier[0]);
    }

    [Fact]
    public void Zero_capacity_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecentLogSink(0));
    }

    private sealed class UnprintableException : Exception
    {
        public override string ToString() => throw new InvalidOperationException("ToString failed");
    }
}
