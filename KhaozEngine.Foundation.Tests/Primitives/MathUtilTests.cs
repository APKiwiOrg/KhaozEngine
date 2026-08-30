using System;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Primitives;

public class MathUtilTests
{
    [Theory]
    [InlineData(-1f, 0f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(2f, 1f)]
    public void Clamp01(float input, float expected) => Assert.Equal(expected, MathUtil.Clamp01(input));

    [Fact]
    public void Lerp_Interpolates() => Assert.Equal(7.5f, MathUtil.Lerp(5f, 10f, 0.5f));

    [Fact]
    public void InverseLerp_Inverts() => Assert.Equal(0.5f, MathUtil.InverseLerp(5f, 10f, 7.5f));

    [Fact]
    public void InverseLerp_DegenerateReturnsZero() => Assert.Equal(0f, MathUtil.InverseLerp(5f, 5f, 7f));

    [Theory]
    [InlineData(-1f, 0f)]     // below the edge clamps to 0
    [InlineData(0f, 0f)]
    [InlineData(5f, 0.5f)]    // midpoint
    [InlineData(10f, 1f)]
    [InlineData(11f, 1f)]     // above the edge clamps to 1
    public void SmoothStep_ClampedHermite(float x, float expected)
        => Assert.Equal(expected, MathUtil.SmoothStep(0f, 10f, x), 1e-6f);

    [Fact]
    public void SmoothStep_DegenerateIsStepFunction()
    {
        Assert.Equal(0f, MathUtil.SmoothStep(5f, 5f, 4.9f));
        Assert.Equal(1f, MathUtil.SmoothStep(5f, 5f, 5f));
    }

    // ---- angle helpers -------------------------------------------------------------------------
    // The interval is HALF-OPEN, (-pi, pi]: exactly -pi comes back as +pi, exactly +pi stays put. Both
    // boundaries are pinned below because a strict-vs-inclusive comparison at the low end is the one
    // place the three hand-rolled copies in the tree disagree with each other.

    [Fact]
    public void WrapAngle_LeavesTheInteriorAlone()
    {
        Assert.Equal(0f, MathUtil.WrapAngle(0f));
        Assert.Equal(1f, MathUtil.WrapAngle(1f));
        Assert.Equal(-1f, MathUtil.WrapAngle(-1f));
    }

    [Fact]
    public void WrapAngle_PiStaysAndMinusPiBecomesPi()
    {
        Assert.Equal(MathF.PI, MathUtil.WrapAngle(MathF.PI));
        Assert.Equal(MathF.PI, MathUtil.WrapAngle(-MathF.PI));
    }

    [Fact]
    public void WrapAngle_FullTurnsCollapseToZero()
    {
        Assert.Equal(0f, MathUtil.WrapAngle(MathF.Tau), 1e-5f);
        Assert.Equal(0f, MathUtil.WrapAngle(-MathF.Tau), 1e-5f);
        Assert.Equal(0f, MathUtil.WrapAngle(MathF.Tau * 10f), 1e-5f);
    }

    [Fact]
    public void WrapAngle_JustPastTheBoundaryFoldsToTheOtherSide()
    {
        Assert.Equal(-MathF.PI + 0.25f, MathUtil.WrapAngle(MathF.PI + 0.25f), 1e-5f);
        Assert.Equal(MathF.PI - 0.25f, MathUtil.WrapAngle(-MathF.PI - 0.25f), 1e-5f);
    }

    [Fact]
    public void WrapAngle_SweepStaysInTheHalfOpenInterval()
    {
        // Fine sweep across five full turns either side, hitting every boundary crossing.
        for (int i = -3141; i <= 3141; i++)
        {
            float a = i * 0.01f;
            float w = MathUtil.WrapAngle(a);
            Assert.True(w > -MathF.PI, $"WrapAngle({a}) = {w} is at or below -pi");
            Assert.True(w <= MathF.PI, $"WrapAngle({a}) = {w} is above pi");
        }
    }

    [Fact]
    public void WrapAngle_IsIdempotent()
    {
        for (int i = -400; i <= 400; i++)
        {
            float a = i * 0.05f;
            float once = MathUtil.WrapAngle(a);
            Assert.Equal(once, MathUtil.WrapAngle(once));
        }
    }

    [Fact]
    public void DeltaAngle_TakesTheShortWayRound()
    {
        // 350 degrees to 10 degrees is +20, not -340.
        float from = 350f * MathF.PI / 180f;
        float to = 10f * MathF.PI / 180f;
        Assert.Equal(20f * MathF.PI / 180f, MathUtil.DeltaAngle(from, to), 1e-5f);
        Assert.Equal(-20f * MathF.PI / 180f, MathUtil.DeltaAngle(to, from), 1e-5f);
    }

    [Fact]
    public void DeltaAngle_ZeroForTheSameHeadingHowEverManyTurnsApart()
    {
        Assert.Equal(0f, MathUtil.DeltaAngle(1.3f, 1.3f));
        Assert.Equal(0f, MathUtil.DeltaAngle(1.3f, 1.3f + MathF.Tau), 1e-5f);
        Assert.Equal(0f, MathUtil.DeltaAngle(1.3f, 1.3f - MathF.Tau * 3f), 1e-4f);
    }

    [Fact]
    public void DeltaAngle_ExactOppositeResolvesToPositivePi()
    {
        Assert.Equal(MathF.PI, MathUtil.DeltaAngle(0f, MathF.PI));
        Assert.Equal(MathF.PI, MathUtil.DeltaAngle(0f, -MathF.PI));
    }

    [Fact]
    public void MoveTowardsAngle_StepsAtMostMaxDelta()
    {
        Assert.Equal(0.1f, MathUtil.MoveTowardsAngle(0f, 1f, 0.1f), 1e-5f);
        Assert.Equal(-0.1f, MathUtil.MoveTowardsAngle(0f, -1f, 0.1f), 1e-5f);
    }

    [Fact]
    public void MoveTowardsAngle_ArrivesWithoutOvershooting()
    {
        Assert.Equal(1f, MathUtil.MoveTowardsAngle(0f, 1f, 5f), 1e-5f);
        Assert.Equal(-1f, MathUtil.MoveTowardsAngle(0f, -1f, 5f), 1e-5f);
    }

    [Fact]
    public void MoveTowardsAngle_CrossesTheWrapSeamTheShortWay()
    {
        // Just under +pi stepping toward just over -pi: the short way is +0.2, across the seam.
        float current = MathF.PI - 0.1f;
        float target = -MathF.PI + 0.1f;
        float stepped = MathUtil.MoveTowardsAngle(current, target, 0.05f);
        Assert.Equal(MathF.PI - 0.05f, stepped, 1e-5f);
        // One more step lands past the seam, wrapped back into the interval rather than growing.
        Assert.Equal(-MathF.PI + 0.1f, MathUtil.MoveTowardsAngle(stepped, target, 0.15f), 1e-5f);
    }

    [Fact]
    public void MoveTowardsAngle_NonPositiveMaxDeltaHoldsStill()
    {
        Assert.Equal(0.5f, MathUtil.MoveTowardsAngle(0.5f, 2f, 0f));
        Assert.Equal(0.5f, MathUtil.MoveTowardsAngle(0.5f, 2f, -1f));
        // Held still, but still wrapped: an accumulated yaw does not leak out of the interval.
        Assert.Equal(0.5f, MathUtil.MoveTowardsAngle(0.5f + MathF.Tau, 2f, 0f), 1e-5f);
    }

    [Fact]
    public void MoveTowardsAngle_ResultAlwaysWrapped()
    {
        float yaw = 3f;
        for (int i = 0; i < 200; i++)
        {
            yaw = MathUtil.MoveTowardsAngle(yaw, yaw + 1f, 0.4f);
            Assert.True(yaw > -MathF.PI && yaw <= MathF.PI, $"yaw escaped the interval: {yaw}");
        }
    }

    [Fact]
    public void LerpAngle_InterpolatesAlongTheShortArc()
    {
        float from = 350f * MathF.PI / 180f;
        float to = 10f * MathF.PI / 180f;
        // Halfway between 350 and 10 degrees is 0, not 180.
        Assert.Equal(0f, MathUtil.LerpAngle(from, to, 0.5f), 1e-5f);
    }

    [Fact]
    public void LerpAngle_EndpointsAreTheWrappedInputs()
    {
        Assert.Equal(1f, MathUtil.LerpAngle(1f, 2f, 0f), 1e-5f);
        Assert.Equal(2f, MathUtil.LerpAngle(1f, 2f, 1f), 1e-5f);
        Assert.Equal(MathF.PI, MathUtil.LerpAngle(-MathF.PI, -MathF.PI, 0f));
    }

    [Fact]
    public void LerpAngle_UnclampedTLikeLerp()
    {
        // t is not clamped, matching Lerp. Overshooting past the target still comes back wrapped.
        float over = MathUtil.LerpAngle(0f, 1f, 2f);
        Assert.Equal(2f, over, 1e-5f);
        Assert.True(over > -MathF.PI && over <= MathF.PI);
    }
}
