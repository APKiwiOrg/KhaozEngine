using System;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// The episode and dump decisions behind <see cref="ThreadPoolStarvationWatchdog"/>, driven with made up readings
/// so no test has to starve its own pool to exercise them.
/// </summary>
public class StarvationEpisodeTrackerTests
{
    private static readonly TimeSpan Episode = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Dump = TimeSpan.FromSeconds(10);
    private static readonly DateTime T0 = new(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc);
    private static readonly PoolCounters Quiet = default;

    private static StarvationEpisodeTracker New() => new(Episode, Dump);

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void Readings_under_the_threshold_open_nothing_and_ask_for_nothing()
    {
        StarvationEpisodeTracker tracker = New();
        for (int i = 0; i < 20; i++)
        {
            StarvationObservation queued = tracker.Observe(T0, Ms(1900), probeCompleted: false, Quiet);
            StarvationObservation ran = tracker.Observe(T0, Ms(3), probeCompleted: true, Quiet);
            Assert.Equal(default, queued);
            Assert.Equal(default, ran);
        }

        Assert.Null(tracker.Open);
    }

    [Fact]
    public void An_episode_spans_every_slow_reading_and_closes_when_a_probe_runs_promptly()
    {
        StarvationEpisodeTracker tracker = New();
        var atPeak = new PoolCounters(40, 90, 1234);

        tracker.Observe(T0.AddSeconds(2), Ms(2000), probeCompleted: false, new PoolCounters(33, 10, 1000));
        tracker.Observe(T0.AddSeconds(5), Ms(5000), probeCompleted: true, atPeak);
        // The next probe is slow too, but not the slowest, so the episode stays open with the peak unchanged.
        tracker.Observe(T0.AddSeconds(8), Ms(3000), probeCompleted: true, new PoolCounters(41, 5, 1300));
        StarvationObservation closing = tracker.Observe(T0.AddSeconds(9), Ms(4), probeCompleted: true, Quiet);

        StarvationEpisode closed = Assert.IsType<StarvationEpisode>(closing.Closed);
        Assert.Equal(T0, closed.StartUtc);
        Assert.Equal(Ms(5000), closed.PeakLatency);
        Assert.Equal(atPeak, closed.CountersAtPeak);
        Assert.Null(tracker.Open);
    }

    [Fact]
    public void A_queued_probe_that_reaches_the_dump_threshold_asks_for_one_dump_per_tracker()
    {
        StarvationEpisodeTracker tracker = New();

        Assert.False(tracker.Observe(T0, Ms(9999), probeCompleted: false, Quiet).CaptureDump);
        Assert.True(tracker.Observe(T0, Ms(10000), probeCompleted: false, Quiet).CaptureDump);
        Assert.False(tracker.Observe(T0, Ms(15000), probeCompleted: false, Quiet).CaptureDump);
        tracker.Observe(T0, Ms(1), probeCompleted: true, Quiet);

        // A second episode, just as bad, gets its line but not a second dump.
        Assert.False(tracker.Observe(T0, Ms(20000), probeCompleted: false, Quiet).CaptureDump);
    }

    [Fact]
    public void A_steady_wait_that_never_reaches_the_dump_threshold_still_asks_once_the_episode_has_lasted_it()
    {
        StarvationEpisodeTracker tracker = New();

        // Probes that each wait about three seconds: none reaches ten, but the episode they form does.
        Assert.False(tracker.Observe(T0.AddSeconds(3), Ms(3000), probeCompleted: false, Quiet).CaptureDump);
        Assert.False(tracker.Observe(T0.AddSeconds(3.25), Ms(3250), probeCompleted: true, Quiet).CaptureDump);
        Assert.False(tracker.Observe(T0.AddSeconds(6.5), Ms(3000), probeCompleted: false, Quiet).CaptureDump);
        Assert.False(tracker.Observe(T0.AddSeconds(6.75), Ms(3250), probeCompleted: true, Quiet).CaptureDump);
        Assert.True(tracker.Observe(T0.AddSeconds(10), Ms(3000), probeCompleted: false, Quiet).CaptureDump);
        Assert.Equal(T0, tracker.Open?.StartUtc);
    }

    [Fact]
    public void A_probe_that_already_ran_does_not_ask_for_a_dump_because_the_starving_stacks_are_gone()
    {
        StarvationEpisodeTracker tracker = New();

        Assert.False(tracker.Observe(T0, Ms(30000), probeCompleted: true, Quiet).CaptureDump);
        Assert.NotNull(tracker.Open);
        Assert.True(tracker.Observe(T0, Ms(10000), probeCompleted: false, Quiet).CaptureDump);
    }

    [Fact]
    public void Thresholds_out_of_order_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StarvationEpisodeTracker(TimeSpan.Zero, Dump));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StarvationEpisodeTracker(Dump, Episode));
    }
}
