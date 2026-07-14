using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.ServerStatus;
using Xunit;

namespace KhaozEngine.Tests.ServerStatus;

public class ServerStatusClientTests
{
    private static ServerStatusReport Healthy(string serverVersion = "1.0.0") =>
        new() { Health = ServerHealth.Healthy, ServerVersion = serverVersion };

    [Fact]
    public async Task PollOnce_Success_StoresReport_AndResetsFailures()
    {
        var source = new FakeServerStatusSource();
        source.Enqueue(Healthy("2.3.4"));
        DateTimeOffset now = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
        var client = new ServerStatusClient(source, clock: () => now);

        ServerStatusSnapshot snap = await client.PollOnceAsync();

        Assert.True(snap.HasReport);
        Assert.Equal("2.3.4", snap.LastReport!.ServerVersion);
        Assert.Equal(now, snap.LastSuccessUtc);
        Assert.Equal(now, snap.LastAttemptUtc);
        Assert.Equal(0, snap.ConsecutiveFailures);
        Assert.Equal(TimeSpan.Zero, snap.StalenessAt(now));
    }

    [Fact]
    public async Task PollOnce_Failure_RetainsLastReport_AndAdvancesStaleness()
    {
        var source = new FakeServerStatusSource();
        DateTimeOffset t0 = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset now = t0;
        var client = new ServerStatusClient(source, clock: () => now);

        source.Enqueue(Healthy("5.0.0"));
        await client.PollOnceAsync();          // success at t0

        source.Enqueue(null);                  // transport miss
        now = t0 + TimeSpan.FromSeconds(30);
        ServerStatusSnapshot snap = await client.PollOnceAsync();

        // The last good report survives the miss. Staleness now measures from the last SUCCESS.
        Assert.True(snap.HasReport);
        Assert.Equal("5.0.0", snap.LastReport!.ServerVersion);
        Assert.Equal(t0, snap.LastSuccessUtc);
        Assert.Equal(now, snap.LastAttemptUtc);
        Assert.Equal(1, snap.ConsecutiveFailures);
        Assert.Equal(TimeSpan.FromSeconds(30), snap.StalenessAt(now));
    }

    [Fact]
    public async Task PollOnce_ConsecutiveFailures_Accumulate_ThenResetOnSuccess()
    {
        var source = new FakeServerStatusSource();
        DateTimeOffset now = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
        var client = new ServerStatusClient(source, clock: () => now);

        source.Enqueue(null);
        source.Enqueue(null);
        await client.PollOnceAsync();
        ServerStatusSnapshot afterTwoMisses = await client.PollOnceAsync();
        Assert.Equal(2, afterTwoMisses.ConsecutiveFailures);
        Assert.False(afterTwoMisses.HasReport);   // never succeeded, so no report retained

        source.Enqueue(Healthy());
        ServerStatusSnapshot afterSuccess = await client.PollOnceAsync();
        Assert.Equal(0, afterSuccess.ConsecutiveFailures);
        Assert.True(afterSuccess.HasReport);
    }

    [Fact]
    public void EmptySnapshot_ReadsAsMaximallyStale_WithNoReport()
    {
        ServerStatusSnapshot empty = ServerStatusSnapshot.Empty;
        Assert.False(empty.HasReport);
        Assert.Equal(TimeSpan.MaxValue, empty.StalenessAt(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task RunAsync_PollsOnAnInterval_UntilCancelled()
    {
        var source = new FakeServerStatusSource();
        for (int i = 0; i < 10; i++)
        {
            source.Enqueue(Healthy());
        }

        using var cts = new CancellationTokenSource();
        int delayCalls = 0;
        DateTimeOffset now = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);

        // Injected delay seam: advance the fake clock by the interval and cancel after 3 polls, so the loop
        // runs deterministically with no real timer.
        Func<TimeSpan, CancellationToken, Task> delay = (interval, ct) =>
        {
            delayCalls++;
            now += interval;
            if (delayCalls >= 3)
            {
                cts.Cancel();
            }
            return Task.CompletedTask;
        };

        var client = new ServerStatusClient(
            source,
            new ServerStatusClientOptions { PollInterval = TimeSpan.FromSeconds(30) },
            clock: () => now,
            delay: delay);

        await client.RunAsync(cts.Token);

        Assert.Equal(3, source.FetchCount);       // poll, delay, poll, delay, poll, delay(cancel)
        Assert.True(client.Current.HasReport);
    }
}
