using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D.Vfx;
using Xunit;

namespace KhaozEngine.Tests.Render2D.Vfx;

/// <summary>Headless coverage for the pure geometry/animation helpers behind the attention beacon renderer.</summary>
public class AttentionBeaconTests
{
    const float Tol = 1e-4f;

    [Fact]
    public void Default_HasSensiblePreset()
    {
        var p = AttentionBeaconParams.Default;
        Assert.Equal(Color.White, p.Color);
        Assert.Equal(1f, p.Intensity, Tol);
        Assert.Equal(3, p.RingCount);
        Assert.Equal(2.4f, p.RingPeriod, Tol);
        Assert.Equal(6f, p.InnerRadius, Tol);
        Assert.Equal(48f, p.MaxRadius, Tol);
        Assert.Equal(1f, p.RingThickness, Tol);
        Assert.Equal(4, p.GlintCount);
        Assert.Equal(28f, p.GlintRadius, Tol);
        Assert.Equal(6f, p.GlintSize, Tol);
        Assert.Equal(6f, p.TwinkleRate, Tol);
        Assert.Equal(GlintStyle.Star, p.GlintStyle);
    }

    [Fact]
    public void BareNew_DrawsNothing_ZeroCounts()
    {
        // A record struct's bare new() is all-zero; that means no rings and no glints (a no-op), by design.
        var p = new AttentionBeaconParams();
        Assert.Equal(0, p.RingCount);
        Assert.Equal(0, p.GlintCount);
    }

    [Fact]
    public void RingPhase_EvenlyStaggered()
    {
        // At time 0 the i-th of 4 rings is offset by i/4 of the period.
        Assert.Equal(0.00f, AttentionBeacon.RingPhase(0, 4, 0f, 2f), Tol);
        Assert.Equal(0.25f, AttentionBeacon.RingPhase(1, 4, 0f, 2f), Tol);
        Assert.Equal(0.50f, AttentionBeacon.RingPhase(2, 4, 0f, 2f), Tol);
        Assert.Equal(0.75f, AttentionBeacon.RingPhase(3, 4, 0f, 2f), Tol);
    }

    [Fact]
    public void RingPhase_WrapsWithinUnitInterval_AndResets()
    {
        // Period 2: phase advances time/period and wraps at the period boundary back to its start.
        float atStart = AttentionBeacon.RingPhase(0, 3, 0f, 2f);
        float atHalf = AttentionBeacon.RingPhase(0, 3, 1f, 2f);
        float atPeriod = AttentionBeacon.RingPhase(0, 3, 2f, 2f);
        Assert.Equal(0.0f, atStart, Tol);
        Assert.Equal(0.5f, atHalf, Tol);
        Assert.Equal(0.0f, atPeriod, Tol); // reset
        Assert.InRange(AttentionBeacon.RingPhase(0, 3, 5f, 2f), 0f, 1f); // always in [0,1)
    }

    [Fact]
    public void RingRadius_GrowsMonotonically_FromInnerToMax()
    {
        float r0 = AttentionBeacon.RingRadius(0f, 6f, 48f);
        float rMid = AttentionBeacon.RingRadius(0.5f, 6f, 48f);
        float r1 = AttentionBeacon.RingRadius(1f, 6f, 48f);
        Assert.Equal(6f, r0, Tol);   // inner at phase 0
        Assert.Equal(27f, rMid, Tol); // lerp midpoint
        Assert.Equal(48f, r1, Tol);  // max at phase 1
        Assert.True(rMid > r0 && r1 > rMid, "radius must grow monotonically with phase");
    }

    [Fact]
    public void RingAlpha_OneAtInner_ZeroAtMax()
    {
        Assert.Equal(1f, AttentionBeacon.RingAlpha(0f), Tol);   // bright at the inner radius
        Assert.Equal(0f, AttentionBeacon.RingAlpha(1f), Tol);   // faded out by the max radius
        Assert.Equal(0.25f, AttentionBeacon.RingAlpha(0.75f), Tol);
    }

    [Fact]
    public void RingDiameter_DefaultThickness_CentersBandOnRadius()
    {
        // bandCenterFraction 0.675: a band at radius 27 needs a quad of side 2*27/0.675 = 80.
        Assert.Equal(80f, AttentionBeacon.RingDiameter(27f, 1f, 0.675f), Tol);
    }

    [Fact]
    public void RingDiameter_ThickerMultiplier_YieldsLargerQuad()
    {
        float thin = AttentionBeacon.RingDiameter(27f, 0.5f, 0.675f);
        float native = AttentionBeacon.RingDiameter(27f, 1f, 0.675f);
        float thick = AttentionBeacon.RingDiameter(27f, 2f, 0.675f);
        Assert.True(thin < native && thick > native, "RingThickness scales the drawn quad");
    }

    [Fact]
    public void GlintAngle_StableAcrossCalls_AndDistinctPerIndex()
    {
        Assert.Equal(AttentionBeacon.GlintAngle(2), AttentionBeacon.GlintAngle(2), Tol); // deterministic
        // Golden-angle spacing: consecutive indices are well separated, none coincide mod tau.
        float a0 = AttentionBeacon.GlintAngle(0);
        float a1 = AttentionBeacon.GlintAngle(1);
        float a2 = AttentionBeacon.GlintAngle(2);
        Assert.True(MathF.Abs(a1 - a0) > 0.1f);
        Assert.True(MathF.Abs(a2 - a1) > 0.1f);
    }

    [Fact]
    public void GlintRadiusFactor_StableAndWithinBand()
    {
        for (int j = 0; j < 8; j++)
        {
            float f = AttentionBeacon.GlintRadiusFactor(j);
            Assert.Equal(f, AttentionBeacon.GlintRadiusFactor(j), Tol); // deterministic
            Assert.InRange(f, 0.6f, 1.0f);
        }
    }

    [Fact]
    public void GlintAlpha_StaysInRange_AndIsNonNegative()
    {
        for (float t = 0f; t < 4f; t += 0.13f)
        {
            float a = AttentionBeacon.GlintAlpha(1, t, 6f);
            Assert.InRange(a, 0f, 1f);
        }
    }

    [Fact]
    public void GlintAlpha_DifferentIndices_TwinkleOutOfPhase()
    {
        // Distinct per-index phase: two glints are not identical at every instant.
        bool differ = false;
        for (float t = 0f; t < 2f; t += 0.1f)
        {
            if (MathF.Abs(AttentionBeacon.GlintAlpha(0, t, 6f) - AttentionBeacon.GlintAlpha(1, t, 6f)) > 1e-3f)
            {
                differ = true;
                break;
            }
        }
        Assert.True(differ, "glints should twinkle on independent phases");
    }
}
