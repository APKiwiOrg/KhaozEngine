using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageCarryLifetimeTests
{
    [Fact]
    public void An_invalid_completion_input_stays_expired_despite_other_safe_runs()
    {
        var summary = CoverageCarryReplay.Summarize("gapped", 1,
            [true, true, false, false, false, true, true, false, false, false, false, false]);
        Assert.Equal(2, summary.Scenarios[0].AlreadyExpiredOnCompletion);
        Assert.Equal(0, summary.Scenarios[0].ReenteredOnCompletion);
        Assert.Equal(0, summary.Scenarios[0].MinimumRemainingSeconds);
        Assert.Equal(0, summary.Scenarios[0].MedianRemainingSeconds);
    }

    [Fact]
    public void A_delayed_anchor_can_reenter_its_cell_and_must_check_the_actual_completion_input()
    {
        var summary = CoverageCarryReplay.Summarize("alternating", 2,
            [true, false, true, false, true, false, true, false, true, false, true, false]);
        Assert.Equal(.5, summary.Scenarios[0].MinimumRemainingSeconds);
        Assert.Equal(.5, summary.Scenarios[0].MedianRemainingSeconds);
        Assert.Equal(0, summary.Scenarios[0].AlreadyExpiredOnCompletion);
        Assert.Equal(0, summary.Scenarios[0].RunsWithReuse);
        Assert.Equal(4, summary.Scenarios[0].ReenteredOnCompletion);
        Assert.Equal(2, summary.Scenarios[0].UnobservedCompletions);
    }

    [Fact]
    public void Delayed_completion_and_reuse_exclude_the_anchor_and_right_censored_run()
    {
        var summary = CoverageCarryReplay.Summarize("hand trace", 2,
            [true, true, false, true, true, true, true, true, true, false, true, true, true]);
        Assert.Equal(2, summary.MinimumFrames);
        Assert.Equal(4, summary.MedianFrames);
        Assert.Equal(3, summary.RightCensoredFrames);
        Assert.Equal(2 / 6.5, summary.ExpiriesPerActorSecond);
        Assert.Equal(32, summary.MaximumSynchronizedActors);
        Assert.Equal(1, summary.Scenarios[0].RunsWithReuse);
        Assert.Equal(0, summary.Scenarios[1].RunsWithReuse);
        Assert.Equal(1, summary.Scenarios[0].MinimumRemainingSeconds);
        Assert.Equal(1.75, summary.Scenarios[0].MedianRemainingSeconds);
        Assert.Equal(0, summary.Scenarios[0].AlreadyExpiredOnCompletion);
        Assert.Equal(1, summary.Scenarios[0].ReenteredOnCompletion);
        Assert.Equal(1, summary.Scenarios[1].RightCensoredCompletions);
    }
}
