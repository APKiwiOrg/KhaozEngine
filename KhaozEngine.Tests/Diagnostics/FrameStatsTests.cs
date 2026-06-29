using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Diagnostics;

public sealed class FrameStatsTests
{
    [Fact]
    public void Fresh_meter_reports_zero()
    {
        var f = new FrameStats();
        Assert.Equal(0f, f.Fps);
        Assert.Equal(0f, f.FrameMsAvg);
        Assert.Equal(0f, f.FrameMsMin);
        Assert.Equal(0f, f.FrameMsMax);
    }

    [Fact]
    public void Steady_60fps_stream_reports_60fps_and_16_7ms()
    {
        var f = new FrameStats();
        for (int i = 0; i < 120; i++) f.Sample(1f / 60f);

        Assert.Equal(60f, f.Fps, 0);            // within 0.5
        Assert.Equal(16.67f, f.FrameMsAvg, 1);  // ~16.7 ms
        Assert.Equal(16.67f, f.FrameMsMin, 1);
        Assert.Equal(16.67f, f.FrameMsMax, 1);
    }

    [Fact]
    public void Min_and_max_track_the_extremes_in_window()
    {
        var f = new FrameStats(windowSeconds: 10f); // wide window so nothing is trimmed
        f.Sample(0.010f); // 10 ms (fast)
        f.Sample(0.050f); // 50 ms (slow)
        f.Sample(0.020f); // 20 ms

        Assert.Equal(10f, f.FrameMsMin, 1);
        Assert.Equal(50f, f.FrameMsMax, 1);
    }

    [Fact]
    public void Old_slow_frame_leaves_window_after_its_time_passes()
    {
        var f = new FrameStats(windowSeconds: 1f);
        f.Sample(0.500f); // one slow 500 ms frame
        Assert.Equal(500f, f.FrameMsMax, 1);

        // ~1.2s of fast frames pushes the slow one out of the rolling window.
        for (int i = 0; i < 120; i++) f.Sample(0.010f);

        Assert.True(f.FrameMsMax < 100f, $"expected slow frame evicted, max={f.FrameMsMax}");
    }

    [Fact]
    public void Nonpositive_and_garbage_dt_are_ignored()
    {
        var f = new FrameStats();
        f.Sample(0f);
        f.Sample(-1f);
        f.Sample(float.NaN);
        f.Sample(float.PositiveInfinity);

        Assert.Equal(0f, f.Fps);
        Assert.False(float.IsNaN(f.FrameMsAvg));
    }

    [Fact]
    public void ManagedBytes_is_positive_in_a_running_process()
    {
        var f = new FrameStats();
        Assert.True(f.ManagedBytes > 0L);
    }
}
