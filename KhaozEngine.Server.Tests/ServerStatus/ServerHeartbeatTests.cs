using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.ServerStatus;
using Xunit;

namespace KhaozEngine.Tests.ServerStatus;

public class ServerHeartbeatTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task InMemorySink_RecordsLastAndCount()
    {
        var sink = new InMemoryServerHeartbeatSink();
        Assert.Null(sink.Last);

        await sink.WriteAsync(new ServerHeartbeat(T0, "1.2.3"));
        await sink.WriteAsync(new ServerHeartbeat(T0.AddSeconds(15), "1.2.3"));

        Assert.Equal(2, sink.WriteCount);
        Assert.Equal(T0.AddSeconds(15), sink.Last!.Value.TimestampUtc);
        Assert.Equal("1.2.3", sink.Last!.Value.ServerVersion);
    }

    [Fact]
    public async Task NullSink_IsANoOp()
    {
        // Must not throw and must accept any heartbeat.
        await NullServerHeartbeatSink.Instance.WriteAsync(new ServerHeartbeat(T0, "9.9.9"));
    }

    [Fact]
    public async Task Tick_WritesImmediately_ThenNotUntilIntervalElapsed()
    {
        var sink = new InMemoryServerHeartbeatSink();
        var svc = new ServerHeartbeatService(sink, "1.0.0", TimeSpan.FromSeconds(15));

        Assert.True(await svc.TickAsync(T0));                        // first beat always due
        Assert.Equal(1, sink.WriteCount);

        Assert.False(await svc.TickAsync(T0.AddSeconds(5)));         // 5 s < 15 s interval, not due
        Assert.False(await svc.TickAsync(T0.AddSeconds(14)));
        Assert.Equal(1, sink.WriteCount);

        Assert.True(await svc.TickAsync(T0.AddSeconds(15)));         // exactly one interval on, due again
        Assert.Equal(2, sink.WriteCount);
    }

    [Fact]
    public async Task Tick_WritesCurrentTimestampAndVersion()
    {
        var sink = new InMemoryServerHeartbeatSink();
        string version = "1.0.0";
        var svc = new ServerHeartbeatService(sink, () => version, TimeSpan.FromSeconds(10));

        await svc.TickAsync(T0);
        Assert.Equal("1.0.0", sink.Last!.Value.ServerVersion);

        version = "1.1.0";                                           // rolling version read fresh each beat
        await svc.TickAsync(T0.AddSeconds(10));
        Assert.Equal(T0.AddSeconds(10), sink.Last!.Value.TimestampUtc);
        Assert.Equal("1.1.0", sink.Last!.Value.ServerVersion);
    }

    [Fact]
    public async Task Tick_ContainsWriteFailure_DoesNotThrow_AndDoesNotStorm()
    {
        var sink = new ThrowingHeartbeatSink();
        var svc = new ServerHeartbeatService(sink, "1.0.0", TimeSpan.FromSeconds(15));

        bool wrote = await svc.TickAsync(T0);                        // sink throws, contained
        Assert.False(wrote);
        Assert.Equal(1, svc.ConsecutiveFailures);
        Assert.NotNull(svc.LastError);

        // Cadence still advanced despite the failure: a within-interval tick is NOT due, so no retry storm.
        Assert.False(await svc.TickAsync(T0.AddSeconds(5)));
        Assert.Equal(1, sink.Attempts);                             // only the one attempt at T0
    }

    [Fact]
    public async Task Tick_FailureThenSuccess_ResetsFailureCounter()
    {
        var sink = new ToggleHeartbeatSink { FailNext = true };
        var svc = new ServerHeartbeatService(sink, "1.0.0", TimeSpan.FromSeconds(10));

        await svc.TickAsync(T0);                                     // fails
        Assert.Equal(1, svc.ConsecutiveFailures);

        sink.FailNext = false;
        bool wrote = await svc.TickAsync(T0.AddSeconds(10));         // succeeds
        Assert.True(wrote);
        Assert.Equal(0, svc.ConsecutiveFailures);
        Assert.Null(svc.LastError);
    }

    [Fact]
    public async Task RunAsync_BeatsOnInterval_UntilCancelled()
    {
        var sink = new InMemoryServerHeartbeatSink();
        using var cts = new CancellationTokenSource();
        DateTimeOffset now = T0;
        int delayCalls = 0;

        Func<TimeSpan, CancellationToken, Task> delay = (interval, ct) =>
        {
            delayCalls++;
            now += interval;
            if (delayCalls >= 2)
            {
                cts.Cancel();
            }
            return Task.CompletedTask;
        };

        var svc = new ServerHeartbeatService(sink, "1.0.0", TimeSpan.FromSeconds(15), delay);
        await svc.RunAsync(() => now, cts.Token);

        Assert.Equal(2, sink.WriteCount);       // beat, delay, beat, delay(cancel)
    }

    private sealed class ThrowingHeartbeatSink : IServerHeartbeatSink
    {
        public int Attempts { get; private set; }

        public Task WriteAsync(ServerHeartbeat heartbeat, CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new InvalidOperationException("db unreachable");
        }
    }

    private sealed class ToggleHeartbeatSink : IServerHeartbeatSink
    {
        public bool FailNext;

        public Task WriteAsync(ServerHeartbeat heartbeat, CancellationToken cancellationToken = default)
        {
            if (FailNext)
            {
                throw new InvalidOperationException("transient");
            }
            return Task.CompletedTask;
        }
    }
}
