using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Persistence;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Queue/batch/salvage semantics of <see cref="BatchedWriter{T}"/>, exercised headlessly against a fake
/// sink (no real store). Mirrors Ruinborne's LedgerWriterTests/ChatLogWriterTests (the game-side wrappers
/// around this same type before it was promoted into the engine), focused on the generic primitive
/// itself: batching, drop-oldest overflow, flush cadence, the whole-batch salvage fallback, shutdown
/// drain, and the null-sink no-op.
/// </summary>
public class BatchedWriterTests
{
    private static int Rec(int n) => n;

    [Fact]
    public async Task Update_does_not_flush_before_the_interval_elapses_then_flushes_once_it_does()
    {
        var sink = new FakeSink();
        var w = new BatchedWriter<int>(sink.WriteAsync, "test", flushIntervalSeconds: 5f);

        w.Enqueue(Rec(1));
        w.Enqueue(Rec(2));

        w.Update(2f);
        Assert.Empty(sink.Batches);   // short of the 5s interval, no write yet

        w.Update(2f);
        Assert.Empty(sink.Batches);   // 4s accumulated, still short

        w.Update(1f);                // 5s accumulated, crosses the interval
        await w.FlushAsync();        // await the off-thread write this Update dispatched

        Assert.Single(sink.Batches);
        Assert.Equal(new[] { 1, 2 }, sink.Batches[0]);
    }

    [Fact]
    public void Overflow_drops_the_oldest_record_and_counts_it()
    {
        var sink = new FakeSink();
        var w = new BatchedWriter<int>(sink.WriteAsync, "test", maxQueue: 2, flushIntervalSeconds: 1f);

        w.Enqueue(Rec(1));
        w.Enqueue(Rec(2));
        w.Enqueue(Rec(3));   // queue is at capacity (2): evicts 1, not 3

        Assert.Equal(1, w.DroppedCount);
    }

    [Fact]
    public async Task Overflow_actually_evicts_the_oldest_not_the_newest()
    {
        var sink = new FakeSink();
        var w = new BatchedWriter<int>(sink.WriteAsync, "test", maxQueue: 2, flushIntervalSeconds: 1f);

        w.Enqueue(Rec(1));
        w.Enqueue(Rec(2));
        w.Enqueue(Rec(3));   // 1 is evicted; 2 and 3 remain

        await w.FlushAsync();

        Assert.Single(sink.Batches);
        Assert.Equal(new[] { 2, 3 }, sink.Batches[0]);
    }

    [Fact]
    public async Task FlushAsync_drains_everything_queued_ignoring_the_interval_and_awaits_the_write()
    {
        var sink = new FakeSink();
        // A long interval that Update() would never cross in this test, to prove FlushAsync bypasses it.
        var w = new BatchedWriter<int>(sink.WriteAsync, "test", flushIntervalSeconds: 999f);

        w.Enqueue(Rec(1));
        w.Enqueue(Rec(2));
        w.Enqueue(Rec(3));

        await w.FlushAsync();   // shutdown-drain path: no Update() call at all

        Assert.Single(sink.Batches);
        Assert.Equal(new[] { 1, 2, 3 }, sink.Batches[0]);

        // The write already landed and was awaited: a second FlushAsync has nothing left to do.
        await w.FlushAsync();
        Assert.Single(sink.Batches);
    }

    [Fact]
    public async Task Empty_queue_update_and_flush_are_clean_noops()
    {
        var sink = new FakeSink();
        var w = new BatchedWriter<int>(sink.WriteAsync, "test", flushIntervalSeconds: 0f);

        w.Update(100f);       // interval already elapsed, but nothing was ever enqueued
        await w.FlushAsync();

        Assert.Empty(sink.Batches);
        Assert.Equal(0, w.DroppedCount);
    }

    [Fact]
    public async Task Null_sink_disables_the_writer_as_a_clean_noop()
    {
        var w = new BatchedWriter<int>(sink: null, label: "test", maxQueue: 1);

        w.Enqueue(Rec(1));
        w.Enqueue(Rec(2));   // would overflow a maxQueue of 1 if the sink were live
        w.Update(1000f);
        await w.FlushAsync();

        Assert.Equal(0, w.DroppedCount);
    }

    // Mirrors Ruinborne LedgerWriterTests.Batch_failure_salvages_the_good_rows_and_drops_only_the_bad_one
    // (issue #43 there): one bad row in a batch must not lose the whole batch. BatchedWriter salvages a
    // failed batch by retrying every record on its own; the good rows land and only the genuinely bad one
    // is dropped and logged.
    [Fact]
    public async Task Batch_failure_salvages_the_good_rows_and_drops_only_the_bad_one()
    {
        var log = new FakeLogger();
        var sink = new FakeSink { PoisonRecord = 13 };
        var w = new BatchedWriter<int>(sink.WriteAsync, "test", log, flushIntervalSeconds: 1f);

        w.Enqueue(Rec(5));
        w.Enqueue(Rec(13));   // the bad row: always fails, alone or in a batch
        w.Enqueue(Rec(7));

        await w.FlushAsync();

        Assert.Contains(sink.Batches, b => b.Count == 1 && b[0] == 5);
        Assert.Contains(sink.Batches, b => b.Count == 1 && b[0] == 7);
        Assert.DoesNotContain(sink.Batches, b => b.Contains(13));
        Assert.Contains(log.Entries, e => e.Message.Contains("dropped 1 record") && e.Message.Contains("13"));
    }

    [Fact]
    public async Task Whole_batch_failure_is_logged_before_salvage_and_the_writer_keeps_working_afterward()
    {
        var log = new FakeLogger();
        var sink = new FakeSink { FailNextBatch = true };
        var w = new BatchedWriter<int>(sink.WriteAsync, "test", log, flushIntervalSeconds: 1f);

        w.Enqueue(Rec(9));
        await w.FlushAsync();
        Assert.Contains(log.Entries, e => e.Message.Contains("batch write") && e.Message.Contains("failed"));

        sink.FailNextBatch = false;
        w.Enqueue(Rec(10));
        await w.FlushAsync();
        Assert.Contains(sink.Batches, b => b.Count == 1 && b[0] == 10);
    }

    private sealed class FakeSink
    {
        public List<List<int>> Batches { get; } = new();
        public bool FailNextBatch { get; set; }
        public int? PoisonRecord { get; set; }

        public Task WriteAsync(IReadOnlyList<int> batch, CancellationToken cancellationToken)
        {
            if (FailNextBatch) { FailNextBatch = false; throw new InvalidOperationException("simulated store outage"); }
            if (PoisonRecord.HasValue && batch.Any(r => r == PoisonRecord.Value))
                throw new InvalidOperationException($"simulated bad row ({PoisonRecord.Value})");
            Batches.Add(batch.ToList());
            return Task.CompletedTask;
        }
    }
}
