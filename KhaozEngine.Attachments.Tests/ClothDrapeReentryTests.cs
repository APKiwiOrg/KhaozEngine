using Xunit;

namespace KhaozEngine.Tests.Attachments;

public sealed class ClothDrapeReentryTests
{
    [Fact]
    public void Recovery_at_the_deadline_uses_the_geometric_entry_time()
    {
        FlatLipReentry gate = Armed();
        FlatLipBearingDecision closing = gate.Observe(1.016, true, .0034f, .001f);
        FlatLipBearingDecision recovered = gate.Observe(1.025, true, .003f, .001f);

        Assert.Equal(FlatLipBearingVerdict.Closing, closing.Verdict);
        Assert.Equal(FlatLipBearingVerdict.Recovered, recovered.Verdict);
        Assert.Equal(1, recovered.EntrySeconds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_first_passing_sample_after_the_geometric_deadline_is_late(bool observeEarly)
    {
        FlatLipReentry gate = Armed();
        if (observeEarly)
            Assert.Equal(FlatLipBearingVerdict.Closing, gate.Observe(1.016, true, .0034f, .001f).Verdict);

        FlatLipBearingDecision late = gate.Observe(1.033, true, .0029f, .001f);

        Assert.Equal(FlatLipBearingVerdict.Late, late.Verdict);
    }

    [Theory]
    [InlineData(.0034f)]
    [InlineData(.0035f)]
    public void An_over_limit_gap_must_decrease(float nextGap)
    {
        FlatLipReentry gate = Armed();
        Assert.Equal(FlatLipBearingVerdict.Closing, gate.Observe(1.008, true, .0034f, .001f).Verdict);

        FlatLipBearingDecision stalled = gate.Observe(1.016, true, nextGap, .001f);

        Assert.Equal(FlatLipBearingVerdict.NotClosing, stalled.Verdict);
    }

    [Theory]
    [InlineData(false, .004f, .001f)]
    [InlineData(true, .004f, .0002f)]
    [InlineData(true, .0029f, .001f)]
    public void Grace_requires_prior_bearing_and_physical_unloading(bool established, float gap, float clearance)
    {
        var gate = new FlatLipReentry(Eligible);
        if (established)
            Assert.Equal(FlatLipBearingVerdict.Bearing, gate.Observe(.8, true, .002f, 0).Verdict);
        gate.Observe(.99, false, gap, clearance);

        FlatLipBearingDecision unsupported = gate.Observe(1.008, true, .0034f, .001f);

        Assert.Equal(FlatLipBearingVerdict.StrictGap, unsupported.Verdict);
    }

    [Fact]
    public void Successful_recovery_cannot_reopen_the_same_window()
    {
        FlatLipReentry gate = Armed();
        Assert.Equal(FlatLipBearingVerdict.Closing, gate.Observe(1.008, true, .0034f, .001f).Verdict);
        Assert.Equal(FlatLipBearingVerdict.Recovered, gate.Observe(1.016, true, .0029f, .001f).Verdict);

        FlatLipBearingDecision reopened = gate.Observe(1.024, true, .0032f, .001f);

        Assert.Equal(FlatLipBearingVerdict.StrictGap, reopened.Verdict);
    }

    private static FlatLipReentry Armed()
    {
        var gate = new FlatLipReentry(Eligible);
        Assert.Equal(FlatLipBearingVerdict.Bearing, gate.Observe(.8, true, .002f, 0).Verdict);
        gate.Observe(.99, false, .004f, .001f);
        return gate;
    }

    private static bool Eligible(double seconds) => seconds < .9 || seconds >= 1;
}
