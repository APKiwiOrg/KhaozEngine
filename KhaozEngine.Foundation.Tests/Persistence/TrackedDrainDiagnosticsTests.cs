using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using KhaozEngine.Persistence;
using Xunit;

namespace KhaozEngine.Tests;

// Sentinels for the tracked drain diagnostics (#1317). Every body and cleanup step is an already
// completed or already faulted task, or a synchronous throw, so nothing sleeps or waits out a watchdog.
public sealed class TrackedDrainDiagnosticsTests
{
    private static readonly string HoldPath = Path.Combine("root", "other.json");
    private static readonly string TargetPath = Path.Combine("root", "save.json");
    private static readonly string InvalidChildPath = Path.Combine(HoldPath, "fail.json");

    [Fact]
    public async Task Primary_body_failure_survives_producer_and_dispose_failures()
    {
        var trace = new TrackedDrainTrace();
        var reports = new List<string>();
        var bodyFailure = new InvalidOperationException("body failed first");
        bool drainRan = false;

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => TrackedDrainRunner.RunAsync(trace,
            () => Task.FromException(bodyFailure),
            [
                new("producer", () => Task.FromException(new TimeoutException("producer cleanup failed"))),
                new("drain", () => { drainRan = true; return Task.CompletedTask; }),
                new("dispose", () => throw new IOException("dispose cleanup failed")),
            ], reports.Add));

        Assert.Same(bodyFailure, thrown);
        Assert.True(drainRan);
        string report = Assert.Single(reports);
        Assert.Contains("primary: System.InvalidOperationException: body failed first", report);
        Assert.Contains("cleanup failures: 2", report);
        Assert.Contains("[producer] System.TimeoutException: producer cleanup failed", report);
        Assert.Contains("[dispose] System.IO.IOException: dispose cleanup failed", report);
        Assert.Contains("cleanup drain completed", report);
    }

    [Fact]
    public async Task Primary_failure_keeps_its_original_throw_site()
    {
        var reports = new List<string>();

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => TrackedDrainRunner.RunAsync(new TrackedDrainTrace(),
            FailInsideBody,
            [new("dispose", () => Task.FromException(new IOException("dispose cleanup failed")))],
            reports.Add));

        Assert.Equal("thrown inside the body", thrown.Message);
        Assert.Contains(nameof(FailInsideBody), thrown.StackTrace);
        Assert.Contains("[dispose] System.IO.IOException", Assert.Single(reports));
    }

    [Fact]
    public async Task Drain_cleanup_is_attempted_after_the_producer_cleanup_fails()
    {
        var trace = new TrackedDrainTrace();
        var producerFailure = new TimeoutException("The controlled writer was not released.");
        bool drainRan = false;
        bool disposeRan = false;

        AggregateException thrown = await Assert.ThrowsAsync<AggregateException>(() => TrackedDrainRunner.RunAsync(trace,
            () => Task.CompletedTask,
            [
                new("producer", () => Task.FromException(producerFailure)),
                new("drain", () => { drainRan = true; return Task.CompletedTask; }),
                new("dispose", () => { disposeRan = true; return Task.CompletedTask; }),
            ], _ => { }));

        Assert.Same(producerFailure, Assert.Single(thrown.InnerExceptions));
        Assert.True(drainRan);
        Assert.True(disposeRan);
        string[] cleanup = trace.Snapshot().Where(entry => entry.Kind == "cleanup").Select(entry => entry.Detail).ToArray();
        Assert.Equal("producer start", cleanup[0]);
        Assert.StartsWith("producer failed System.TimeoutException", cleanup[1]);
        Assert.Equal(new[] { "drain start", "drain completed", "dispose start", "dispose completed" }, cleanup.Skip(2));
    }

    [Fact]
    public async Task Successful_body_with_a_failed_dispose_still_fails()
    {
        var reports = new List<string>();
        var disposeFailure = new ObjectDisposedException("queue");

        AggregateException thrown = await Assert.ThrowsAsync<AggregateException>(() => TrackedDrainRunner.RunAsync(new TrackedDrainTrace(),
            () => Task.CompletedTask,
            [new("dispose", () => throw disposeFailure)],
            reports.Add));

        Assert.Same(disposeFailure, Assert.Single(thrown.InnerExceptions));
        Assert.Contains("body succeeded but cleanup", thrown.Message);
        string report = Assert.Single(reports);
        Assert.Contains("primary: none", report);
        Assert.Contains("[dispose] System.ObjectDisposedException", report);
    }

    [Fact]
    public async Task All_success_completes_and_emits_one_report()
    {
        var trace = new TrackedDrainTrace();
        var reports = new List<string>();

        await TrackedDrainRunner.RunAsync(trace, () => Task.CompletedTask,
            [new("producer", () => Task.CompletedTask), new("dispose", () => Task.CompletedTask)],
            reports.Add);

        string report = Assert.Single(reports);
        Assert.Contains("primary: none", report);
        Assert.Contains("cleanup failures: 0", report);
        Assert.Contains("Late WriteFailed delivery after final drain may be absent", report);
        Assert.Equal(
            new[] { "body start", "body completed", "cleanup producer start", "cleanup producer completed", "cleanup dispose start", "cleanup dispose completed" },
            trace.Snapshot().Select(entry => $"{entry.Kind} {entry.Detail}"));
    }

    [Fact]
    public async Task Report_emission_failure_after_a_successful_body_fails()
    {
        var emitFailure = new InvalidOperationException("output helper rejected the report");

        AggregateException thrown = await Assert.ThrowsAsync<AggregateException>(() => TrackedDrainRunner.RunAsync(new TrackedDrainTrace(),
            () => Task.CompletedTask, [], _ => throw emitFailure));

        Assert.Same(emitFailure, Assert.Single(thrown.InnerExceptions));
    }

    [Fact]
    public async Task Report_emission_failure_keeps_the_primary_first_with_its_throw_site()
    {
        var emitFailure = new InvalidOperationException("output helper rejected the report");
        var disposeFailure = new IOException("dispose cleanup failed");

        AggregateException thrown = await Assert.ThrowsAsync<AggregateException>(() => TrackedDrainRunner.RunAsync(new TrackedDrainTrace(),
            FailInsideBody, [new("dispose", () => Task.FromException(disposeFailure))], _ => throw emitFailure));

        Assert.Equal(3, thrown.InnerExceptions.Count);
        Assert.Equal("thrown inside the body", thrown.InnerExceptions[0].Message);
        Assert.Contains(nameof(FailInsideBody), thrown.InnerExceptions[0].StackTrace);
        Assert.Same(emitFailure, thrown.InnerExceptions[1]);
        Assert.Same(disposeFailure, thrown.InnerExceptions[2]);
        Assert.Contains("[dispose] System.IO.IOException", thrown.Message);
    }

    [Fact]
    public async Task Only_the_invalid_child_path_completes_the_expected_failure_signal()
    {
        var trace = new TrackedDrainTrace();
        TrackedDrainFailureSignal signal = NewSignal(trace);
        var expected = new PersistenceWriteFailedEventArgs(InvalidChildPath, new IOException("parent is a file"), 1);

        signal.Observe(null, expected);

        Assert.True(signal.Expected.IsCompletedSuccessfully);
        Assert.Same(expected, await signal.Expected);
        signal.AssertNoUnexpected();
        TrackedDrainTraceEntry entry = Assert.Single(trace.Snapshot());
        Assert.Equal("write-failed", entry.Kind);
        Assert.StartsWith("role=invalid-child expected=True attempts=1 error=System.IO.IOException", entry.Detail);
    }

    [Fact]
    public async Task A_hold_path_failure_faults_the_signal_instead_of_completing_it()
    {
        var trace = new TrackedDrainTrace();
        TrackedDrainFailureSignal signal = NewSignal(trace);
        var holdTimeout = new TimeoutException("The controlled writer was not released.");

        signal.Observe(null, new PersistenceWriteFailedEventArgs(HoldPath, holdTimeout, 1));
        signal.Observe(null, new PersistenceWriteFailedEventArgs(InvalidChildPath, new IOException("parent is a file"), 1));

        InvalidOperationException fault = await Assert.ThrowsAsync<InvalidOperationException>(() => signal.Expected);
        Assert.Contains("hold path", fault.Message);
        Assert.Same(holdTimeout, fault.InnerException);
        AggregateException unexpected = Assert.Throws<AggregateException>(signal.AssertNoUnexpected);
        Assert.Same(holdTimeout, Assert.Single(unexpected.InnerExceptions));
        Assert.Equal(
            new[] { "role=hold expected=False", "role=invalid-child expected=True" },
            trace.Snapshot().Select(entry => string.Join(' ', entry.Detail.Split(' ').Take(2))));
    }

    [Fact]
    public void A_late_unexpected_failure_is_reported_after_the_expected_signal()
    {
        TrackedDrainFailureSignal signal = NewSignal(new TrackedDrainTrace());
        var targetFailure = new IOException("target write failed");

        signal.Observe(null, new PersistenceWriteFailedEventArgs(InvalidChildPath, new IOException("parent is a file"), 1));
        signal.Observe(null, new PersistenceWriteFailedEventArgs(TargetPath, targetFailure, 1));

        Assert.True(signal.Expected.IsCompletedSuccessfully);
        AggregateException unexpected = Assert.Throws<AggregateException>(signal.AssertNoUnexpected);
        Assert.Same(targetFailure, Assert.Single(unexpected.InnerExceptions));
        Assert.Contains("tracked-target", unexpected.Message);
    }

    [Fact]
    public void A_near_miss_of_the_invalid_child_path_is_not_the_expected_signal()
    {
        TrackedDrainFailureSignal signal = NewSignal(new TrackedDrainTrace());

        signal.Observe(null, new PersistenceWriteFailedEventArgs(Path.Combine(HoldPath, "FAIL.json"), new IOException("near miss"), 1));

        Assert.True(signal.Expected.IsFaulted);
        AggregateException unexpected = Assert.Throws<AggregateException>(signal.AssertNoUnexpected);
        Assert.Contains("unknown", unexpected.Message);
    }

    [Fact]
    public void Trace_capacity_is_finite_and_truncation_is_visible()
    {
        Assert.Equal(256, TrackedDrainTrace.DefaultCapacity);
        var trace = new TrackedDrainTrace(capacity: 4);

        for (int i = 1; i <= 7; i++)
        {
            trace.Record("entry", i.ToString(CultureInfo.InvariantCulture));
        }

        Assert.Equal(new long[] { 1, 2, 3, 4 }, trace.Snapshot().Select(entry => entry.Sequence));
        Assert.Equal(3L, trace.Dropped);
        string rendered = trace.Render();
        Assert.Contains("trace: 4 of capacity 4 kept, 3 dropped, 7 recorded", rendered);
        Assert.Contains("TRUNCATED: entries after sequence 4 were dropped, 3 dropped", rendered);
        Assert.DoesNotContain(" entry 5", rendered);
    }

    [Fact]
    public void Trace_rejects_a_capacity_below_one()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new TrackedDrainTrace(capacity: 0));

    [Fact]
    public void Trace_sequence_and_timestamps_are_monotonic_across_threads()
    {
        var trace = new TrackedDrainTrace();

        Parallel.For(0, 64, i => trace.Record("entry", i.ToString(CultureInfo.InvariantCulture)));

        IReadOnlyList<TrackedDrainTraceEntry> entries = trace.Snapshot();
        Assert.Equal(Enumerable.Range(1, 64).Select(i => (long)i), entries.Select(entry => entry.Sequence));
        for (int i = 1; i < entries.Count; i++)
        {
            Assert.True(entries[i].Timestamp >= entries[i - 1].Timestamp, $"Timestamp went backwards at sequence {entries[i].Sequence}.");
        }

        Assert.All(entries, entry => Assert.True(entry.ThreadId > 0));
        Assert.Equal(0L, trace.Dropped);
    }

    [Fact]
    public void Guard_records_a_callback_failure_at_its_origin_and_rethrows_it()
    {
        var trace = new TrackedDrainTrace();
        var failure = new TimeoutException("The drain decision was not released.");

        TimeoutException thrown = Assert.Throws<TimeoutException>(() => trace.Guard("drain-decision", () => throw failure));

        Assert.Same(failure, thrown);
        TrackedDrainTraceEntry entry = Assert.Single(trace.Snapshot());
        Assert.Equal("callback-failed", entry.Kind);
        Assert.Equal("drain-decision System.TimeoutException: The drain decision was not released.", entry.Detail);
    }

    [Fact]
    public void Gate_records_release_request_entry_and_release_observation_in_order()
    {
        var trace = new TrackedDrainTrace();
        using var gate = new TrackedDrainGate("writer", TimeSpan.FromSeconds(10), "The controlled writer was not released.", trace);

        gate.Release("test");
        gate.Hold();

        Assert.True(gate.Entered.Task.IsCompletedSuccessfully);
        Assert.Equal(
            new[] { "gate-release-requested writer by test", "gate-entered writer", "gate-released writer" },
            trace.Snapshot().Select(entry => $"{entry.Kind} {entry.Detail}"));
    }

    [Fact]
    public void Gate_records_a_timeout_and_throws_its_message()
    {
        var trace = new TrackedDrainTrace();
        // A zero timeout makes the unreleased wait return false at once, so no watchdog is waited out.
        using var gate = new TrackedDrainGate("supersession", TimeSpan.Zero, "The controlled writer was not released.", trace);

        TimeoutException thrown = Assert.Throws<TimeoutException>(gate.Hold);

        Assert.Equal("The controlled writer was not released.", thrown.Message);
        Assert.True(gate.Entered.Task.IsCompletedSuccessfully);
        Assert.Equal(
            new[] { "gate-entered supersession", "gate-timeout supersession" },
            trace.Snapshot().Select(entry => $"{entry.Kind} {entry.Detail}"));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task FailInsideBody() => throw new InvalidOperationException("thrown inside the body");

    private static TrackedDrainFailureSignal NewSignal(TrackedDrainTrace trace)
        => new(trace, InvalidChildPath, new Dictionary<string, string>
        {
            [HoldPath] = "hold",
            [TargetPath] = "tracked-target",
            [InvalidChildPath] = "invalid-child",
        });
}
