using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.ServerStatus;
using Xunit;

namespace KhaozEngine.Tests.ServerStatus;

/// <summary>
/// Drives <see cref="IdleShutdownService"/> with an explicit clock, so every boundary is asserted rather
/// than waited for. The service exists to stop a billed-by-the-second server head, so the two failures that
/// matter are opposite: never shutting down (costs money) and shutting down while someone is playing (costs
/// a session). The tests below pin both edges.
/// </summary>
public sealed class IdleShutdownServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Requests_shutdown_only_once_the_full_window_has_elapsed_empty()
    {
        IdleShutdownService svc = new(() => 0, TimeSpan.FromMinutes(60));

        Assert.False(svc.Tick(T0));                              // streak starts here
        Assert.False(svc.Tick(T0.AddMinutes(59)));               // one minute short
        Assert.False(svc.Tick(T0.AddSeconds(3599)));             // one second short
        Assert.True(svc.Tick(T0.AddMinutes(60)));                // exactly on the boundary
    }

    [Fact]
    public void Fires_exactly_once_per_empty_streak()
    {
        int raised = 0;
        IdleShutdownService svc = new(() => 0, TimeSpan.FromMinutes(60));
        svc.IdleShutdownRequested += () => raised++;

        svc.Tick(T0);
        Assert.True(svc.Tick(T0.AddMinutes(60)));

        // A host that does not exit immediately keeps ticking. It must not be told again.
        Assert.False(svc.Tick(T0.AddMinutes(61)));
        Assert.False(svc.Tick(T0.AddMinutes(600)));
        Assert.Equal(1, raised);
        Assert.True(svc.HasRequestedShutdown);
    }

    [Fact]
    public void A_single_player_arriving_resets_the_window_and_the_latch()
    {
        int players = 0;
        IdleShutdownService svc = new(() => players, TimeSpan.FromMinutes(60));

        svc.Tick(T0);
        Assert.True(svc.Tick(T0.AddMinutes(60)));
        Assert.True(svc.HasRequestedShutdown);

        players = 1;
        Assert.False(svc.Tick(T0.AddMinutes(61)));
        Assert.False(svc.HasRequestedShutdown);
        Assert.Null(svc.EmptySinceUtc);

        // They leave again: the server gets a FULL fresh window, not the remainder of the old one.
        players = 0;
        Assert.False(svc.Tick(T0.AddMinutes(62)));
        Assert.False(svc.Tick(T0.AddMinutes(121)));
        Assert.True(svc.Tick(T0.AddMinutes(122)));
    }

    [Fact]
    public void A_player_present_throughout_never_triggers_a_shutdown()
    {
        IdleShutdownService svc = new(() => 1, TimeSpan.FromMinutes(60));

        for (int minute = 0; minute <= 600; minute += 10)
        {
            Assert.False(svc.Tick(T0.AddMinutes(minute)));
        }

        Assert.Null(svc.EmptySinceUtc);
        Assert.False(svc.HasRequestedShutdown);
    }

    [Fact]
    public void A_failing_player_count_is_treated_as_occupied_never_as_empty()
    {
        // The dangerous read is a broken one. "Unknown" must never be mistaken for "nobody is here", or a
        // transient database blip shuts down a live server full of players.
        IdleShutdownService svc = new(() => throw new InvalidOperationException("count source down"),
                                      TimeSpan.FromMinutes(60));

        Assert.False(svc.Tick(T0));
        Assert.False(svc.Tick(T0.AddMinutes(600)));
        Assert.Null(svc.EmptySinceUtc);
        Assert.False(svc.HasRequestedShutdown);
    }

    [Fact]
    public void Disabled_service_never_requests_anything()
    {
        int raised = 0;
        IdleShutdownService svc = new(() => 0, TimeSpan.FromMinutes(60), enabled: false);
        svc.IdleShutdownRequested += () => raised++;

        Assert.False(svc.Tick(T0));
        Assert.False(svc.Tick(T0.AddMinutes(600)));
        Assert.Equal(0, raised);
        Assert.Null(svc.RemainingBeforeShutdown(T0.AddMinutes(600)));
    }

    [Fact]
    public void Remaining_counts_down_and_floors_at_zero()
    {
        IdleShutdownService svc = new(() => 0, TimeSpan.FromMinutes(60));

        Assert.Null(svc.RemainingBeforeShutdown(T0));            // no streak until the first tick
        svc.Tick(T0);
        Assert.Equal(TimeSpan.FromMinutes(60), svc.RemainingBeforeShutdown(T0));
        Assert.Equal(TimeSpan.FromMinutes(15), svc.RemainingBeforeShutdown(T0.AddMinutes(45)));
        Assert.Equal(TimeSpan.Zero, svc.RemainingBeforeShutdown(T0.AddMinutes(90)));
    }

    [Fact]
    public async Task RunAsync_returns_once_the_window_elapses()
    {
        List<TimeSpan> waits = [];
        IdleShutdownService svc = new(
            () => 0,
            TimeSpan.FromMilliseconds(30),
            pollInterval: TimeSpan.FromMilliseconds(10),
            delay: (d, _) => { waits.Add(d); return Task.CompletedTask; });

        await svc.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(svc.HasRequestedShutdown);
        Assert.All(waits, w => Assert.Equal(TimeSpan.FromMilliseconds(10), w));
    }

    [Fact]
    public async Task RunAsync_returns_without_requesting_when_cancelled_first()
    {
        using CancellationTokenSource cts = new();
        IdleShutdownService svc = new(
            () => 0,
            TimeSpan.FromHours(1),
            pollInterval: TimeSpan.FromMilliseconds(1),
            delay: (_, _) => { cts.Cancel(); return Task.CompletedTask; });

        await svc.RunAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(svc.HasRequestedShutdown);
    }

    [Fact]
    public async Task RunAsync_on_a_disabled_service_returns_immediately_without_polling()
    {
        bool delayed = false;
        IdleShutdownService svc = new(
            () => 0,
            TimeSpan.FromMilliseconds(1),
            enabled: false,
            delay: (_, _) => { delayed = true; return Task.CompletedTask; });

        await svc.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(delayed);
        Assert.False(svc.HasRequestedShutdown);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_idle_window_is_rejected(int minutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new IdleShutdownService(() => 0, TimeSpan.FromMinutes(minutes)));
    }

    [Fact]
    public void A_non_positive_poll_interval_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new IdleShutdownService(() => 0, TimeSpan.FromMinutes(60), pollInterval: TimeSpan.Zero));
    }

    [Fact]
    public void A_null_player_count_source_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new IdleShutdownService(null!, TimeSpan.FromMinutes(60)));
    }
}
