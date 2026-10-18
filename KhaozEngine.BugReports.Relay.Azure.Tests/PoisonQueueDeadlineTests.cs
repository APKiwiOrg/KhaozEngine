using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Relay;
using KhaozEngine.BugReports.Relay.Azure;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay.Azure;

/// <summary>Run and cleanup deadlines are driven deterministically without waits or retries.</summary>
public sealed class PoisonQueueDeadlineTests
{
    const string Id = "01900000000070008000000000000001";

    [Fact]
    public async Task OwnDeadline_MidBatch_ReturnsMovedCountAndReleasesTail()
    {
        var time = new DeadlineClock();
        var poison = TwoMessages();
        poison.AfterDelete = () => time.Advance(TimeSpan.FromHours(1));
        var work = new RecordingQueueSender();

        Assert.Equal(1, await new PoisonQueueRequeuer(poison, work, time: time).RequeueAsync(default));

        Assert.Single(work.Messages);
        Assert.Equal("second", Assert.Single(poison.Messages).Id);
        Assert.Equal(new[] { "first" }, poison.Deleted);
        Assert.Equal(new[] { "second" }, poison.Released);
        Assert.Equal(TimeSpan.FromHours(2), Assert.Single(poison.Visibility));
    }

    [Fact]
    public async Task BeforeDeadline_ContinuesDraining()
    {
        var time = new DeadlineClock();
        var poison = TwoMessages();
        poison.AfterDelete = () => time.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(2, await new PoisonQueueRequeuer(poison, new RecordingQueueSender(), time: time).RequeueAsync(default));
        Assert.Empty(poison.Messages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiveDeadline_ReturnsCount_UnlessCallerAlsoCancelled(bool cancelCaller)
    {
        using var caller = new CancellationTokenSource();
        var time = new DeadlineClock();
        var poison = TwoMessages();
        poison.BeforeReceive = ct =>
        {
            time.Advance(TimeSpan.FromHours(1));
            if (cancelCaller) caller.Cancel();
            ct.ThrowIfCancellationRequested();
        };
        Task<int> run = new PoisonQueueRequeuer(poison, new RecordingQueueSender(), time: time).RequeueAsync(caller.Token);
        if (cancelCaller)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        else
            Assert.Equal(0, await run);
        Assert.Empty(poison.Deleted);
    }

    [Fact]
    public async Task CleanupDeadline_LogsAndPreservesSuccessfulCount()
    {
        var time = new DeadlineClock();
        var poison = TwoMessages();
        poison.Messages[1] = new PoisonQueueMessage("capped", "receipt", ReportWorkItemJson.Serialize(new(ReportWorkKind.CreateIssue, Id, 3)));
        poison.BeforeRelease = ct =>
        {
            time.Advance(TimeSpan.FromSeconds(30));
            ct.ThrowIfCancellationRequested();
        };
        var log = new PoisonQueueRequeuerTests.RecordingLogger();

        Assert.Equal(1, await new PoisonQueueRequeuer(poison, new RecordingQueueSender(), log: log, time: time).RequeueAsync(default));

        Assert.Equal("capped", Assert.Single(poison.Messages).Id);
        Assert.Equal(2, log.Warnings.Count);
        Assert.Contains("could not be made visible", log.Warnings[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task OwnDeadline_CleanupFault_DoesNotReplaceMovedCount()
    {
        var time = new DeadlineClock();
        var poison = TwoMessages();
        poison.AfterDelete = () => time.Advance(TimeSpan.FromHours(1));
        poison.BeforeRelease = _ => throw new InvalidOperationException("cleanup failed");
        var log = new PoisonQueueRequeuerTests.RecordingLogger();

        Assert.Equal(1, await new PoisonQueueRequeuer(poison, new RecordingQueueSender(), log: log, time: time).RequeueAsync(default));

        Assert.Contains("second", Assert.Single(log.Warnings), StringComparison.Ordinal);
    }

    static PoisonQueueRequeuerTests.MemoryPoisonQueue TwoMessages()
    {
        var poison = new PoisonQueueRequeuerTests.MemoryPoisonQueue();
        poison.Add("first", new(ReportWorkKind.CreateIssue, Id));
        poison.Add("second", new(ReportWorkKind.CreateIssue, Id));
        return poison;
    }

    sealed class DeadlineClock : TimeProvider
    {
        readonly List<DeadlineTimer> timers = new();
        DateTimeOffset now = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            var timer = new DeadlineTimer(callback, state, now + dueTime);
            timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan amount)
        {
            now += amount;
            foreach (DeadlineTimer timer in timers.ToArray())
                timer.FireIfDue(now);
        }

        sealed class DeadlineTimer(TimerCallback callback, object? state, DateTimeOffset due) : ITimer
        {
            bool disposed;
            public void FireIfDue(DateTimeOffset now)
            {
                if (disposed || now < due) return;
                disposed = true;
                callback(state);
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
