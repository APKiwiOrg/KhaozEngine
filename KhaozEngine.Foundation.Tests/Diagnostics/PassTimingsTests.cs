using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Diagnostics;

public sealed class PassTimingsTests
{
    [Fact]
    public void Fresh_meter_has_no_passes()
    {
        var t = new PassTimings();
        Assert.Empty(t.PassNames);
    }

    [Fact]
    public void Unsampled_pass_reads_zero()
    {
        var t = new PassTimings();
        Assert.Equal(0f, t.AvgMs("shadow"));
        Assert.Equal(0f, t.MinMs("shadow"));
        Assert.Equal(0f, t.MaxMs("shadow"));
    }

    [Fact]
    public void First_sample_creates_the_pass_in_order()
    {
        var t = new PassTimings();
        t.Sample("post", 2f);
        t.Sample("shadow", 1f);
        t.Sample("model", 3f);

        Assert.Equal(new[] { "post", "shadow", "model" }, t.PassNames);
    }

    [Fact]
    public void Steady_stream_reports_that_passs_avg_min_max()
    {
        var t = new PassTimings(windowSeconds: 10f); // wide window, nothing trimmed
        for (int i = 0; i < 10; i++) t.Sample("model", 5f);

        Assert.Equal(5f, t.AvgMs("model"), 2);
        Assert.Equal(5f, t.MinMs("model"), 2);
        Assert.Equal(5f, t.MaxMs("model"), 2);
    }

    [Fact]
    public void Min_and_max_track_the_extremes_per_pass()
    {
        var t = new PassTimings(windowSeconds: 10f);
        t.Sample("model", 2f);
        t.Sample("model", 8f);
        t.Sample("model", 4f);

        Assert.Equal(2f, t.MinMs("model"), 2);
        Assert.Equal(8f, t.MaxMs("model"), 2);
    }

    [Fact]
    public void Passes_are_tracked_independently()
    {
        var t = new PassTimings(windowSeconds: 10f);
        for (int i = 0; i < 5; i++) t.Sample("shadow", 1f);
        for (int i = 0; i < 5; i++) t.Sample("post", 9f);

        Assert.Equal(1f, t.AvgMs("shadow"), 2);
        Assert.Equal(9f, t.AvgMs("post"), 2);
    }

    [Fact]
    public void Old_slow_sample_leaves_window_after_its_time_passes()
    {
        var t = new PassTimings(windowSeconds: 1f);
        t.Sample("model", 500f); // one slow 500ms pass
        Assert.Equal(500f, t.MaxMs("model"), 1);

        // ~1.2s (in ms-equivalent window terms) of fast samples pushes the slow one out.
        for (int i = 0; i < 130; i++) t.Sample("model", 10f);

        Assert.True(t.MaxMs("model") < 100f, $"expected slow sample evicted, max={t.MaxMs("model")}");
    }

    [Fact]
    public void Nonpositive_and_garbage_ms_are_ignored()
    {
        var t = new PassTimings();
        t.Sample("model", 0f);
        t.Sample("model", -1f);
        t.Sample("model", float.NaN);
        t.Sample("model", float.PositiveInfinity);

        Assert.Empty(t.PassNames); // never a valid sample, so the pass was never created
        Assert.Equal(0f, t.AvgMs("model"));
        Assert.False(float.IsNaN(t.AvgMs("model")));
    }

    [Fact]
    public void Null_or_empty_pass_name_is_ignored()
    {
        var t = new PassTimings();
        t.Sample("", 5f);
        t.Sample(null!, 5f);
        Assert.Empty(t.PassNames);
    }

    [Fact]
    public void Reset_clears_every_pass()
    {
        var t = new PassTimings();
        t.Sample("model", 5f);
        t.Sample("post", 2f);
        Assert.Equal(2, t.PassNames.Count);

        t.Reset();
        Assert.Empty(t.PassNames);
        Assert.Equal(0f, t.AvgMs("model"));
    }
}
