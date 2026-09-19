using KhaozEngine.SegmentRig;
using Xunit;

namespace KhaozEngine.Tests.SegmentRig;

/// <summary>The style dispatch: that it picks the curve and nothing else does, and that an unset style is the
/// bare-handed one.</summary>
/// <remarks>
/// WHICH style a body throws is the GAME's question, off whatever is in the hand, and the package does not
/// answer it. So there is no test here for the selection or for a cadence: both read game content and a tick
/// rate, and both stay on the consumer's side of the seam.
/// </remarks>
public class AttackStyleTests
{
    [Fact]
    public void TheStylePicksTheCurveAndNothingElseDoes()
    {
        for (int i = 0; i <= 20; i++)
        {
            float phase = i / 20f;
            Assert.Equal(AttackSwing.PoseAt(phase), AttackStyles.PoseAt(AttackStyle.Punch, phase));
            Assert.Equal(SlashSwing.PoseAt(phase), AttackStyles.PoseAt(AttackStyle.Slash, phase));
        }

        // The two are really different poses, which is what the whole indirection is for: a slash that had
        // been wired to the punch's curve would pass every phase test in both files.
        Assert.NotEqual(AttackSwing.PoseAt(0f), SlashSwing.PoseAt(0f));
        Assert.NotEqual(AttackSwing.PoseAt(0.5f), SlashSwing.PoseAt(0.5f));
    }

    [Fact]
    public void ThePunchIsTheDefaultSoAnUnsetStyleIsTheBareHandedOne() =>
        Assert.Equal(AttackStyle.Punch, default(AttackStyle));
}
