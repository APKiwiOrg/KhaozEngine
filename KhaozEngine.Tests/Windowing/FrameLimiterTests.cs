using System.Collections.Generic;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

/// <summary>
/// The software frame-rate cap that <see cref="AppWindow.Run"/> uses to pace the loop to a target Hz (so a game can
/// pin its render rate to an integer multiple of its network tick regardless of whether the swapchain's vsync
/// actually throttles on a given backend). Pure scheduling math, fed a monotonic clock; the loop does the waiting.
/// </summary>
public class FrameLimiterTests
{
    [Fact]
    public void Uncapped_limiter_is_disabled_and_never_waits()
    {
        var fl = new FrameLimiter(0);
        Assert.False(fl.Enabled);
        Assert.Equal(0.0, fl.WaitBeforeNext(0.0), 9);
        Assert.Equal(0.0, fl.WaitBeforeNext(1234.5), 9);
    }

    [Fact]
    public void Negative_target_is_treated_as_uncapped()
    {
        Assert.False(new FrameLimiter(-30).Enabled);
    }

    [Fact]
    public void Capped_limiter_paces_frame_starts_to_the_target_period()
    {
        var fl = new FrameLimiter(100);   // 10 ms period
        Assert.True(fl.Enabled);

        double now = 0.0;
        var starts = new List<double>();
        for (int i = 0; i < 60; i++)
        {
            double wait = fl.WaitBeforeNext(now);
            now += wait;        // "sleep" for the requested wait
            starts.Add(now);    // this frame starts
            now += 0.001;       // 1 ms of frame work
        }

        // Once settled, consecutive frame starts sit ~10 ms apart (the cap), never materially faster.
        for (int i = 10; i < starts.Count; i++)
            Assert.InRange(starts[i] - starts[i - 1], 0.0099, 0.0101);
    }

    [Fact]
    public void Frames_faster_than_the_cap_are_slowed_to_it()
    {
        // Zero-work frames polled far faster than the cap must still be spaced at the target period.
        var fl = new FrameLimiter(60);   // ~16.67 ms
        double now = 0.0;
        double last = double.NaN;
        var gaps = new List<double>();
        for (int i = 0; i < 40; i++)
        {
            now += fl.WaitBeforeNext(now);
            if (!double.IsNaN(last)) gaps.Add(now - last);
            last = now;
            // no work, no extra idle: the limiter alone must impose the cadence
        }
        for (int i = 5; i < gaps.Count; i++)
            Assert.InRange(gaps[i], 1.0 / 60.0 - 1e-4, 1.0 / 60.0 + 1e-4);
    }

    [Fact]
    public void A_long_stall_does_not_bank_a_catch_up_burst()
    {
        var fl = new FrameLimiter(100);   // 10 ms
        fl.WaitBeforeNext(0.0);           // prime
        // A 500 ms stall (GC / OS sleep). The limiter must NOT try to reclaim the lost time by scheduling a burst of
        // zero-wait frames; it re-anchors and caps immediately going forward.
        Assert.Equal(0.0, fl.WaitBeforeNext(0.5), 9);   // behind schedule -> no wait, re-anchor to now
        double wait = fl.WaitBeforeNext(0.501);         // 1 ms later -> asks to wait ~9 ms, not 0
        Assert.InRange(wait, 0.008, 0.010);
    }
}
