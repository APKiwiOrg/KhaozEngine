using System;
using KhaozEngine.Particles;
using Xunit;

namespace KhaozEngine.Tests.Particles;

public class ParticleCurveTests
{
    private static float S(float x) => x * x * (3f - 2f * x);

    [Fact]
    public void Default_IsLinearIdentity()
    {
        var curve = default(ParticleCurve);
        Assert.Equal(ParticleCurveKind.Linear, curve.Kind);
        for (float n = 0f; n <= 1f; n += 0.05f)
        {
            Assert.Equal(n, curve.Evaluate(n));
        }
    }

    [Fact]
    public void Linear_ReturnsExactN()
    {
        var curve = ParticleCurve.Linear;
        // Bit-identical to the argument, not merely close.
        Assert.Equal(0f, curve.Evaluate(0f));
        Assert.Equal(0.37f, curve.Evaluate(0.37f));
        Assert.Equal(1f, curve.Evaluate(1f));
    }

    [Fact]
    public void EaseIn_IsNSquared()
    {
        var curve = ParticleCurve.EaseIn;
        Assert.Equal(0f, curve.Evaluate(0f));
        Assert.Equal(0.25f, curve.Evaluate(0.5f), 6);
        Assert.Equal(1f, curve.Evaluate(1f), 6);
    }

    [Fact]
    public void EaseOut_IsComplementSquared()
    {
        var curve = ParticleCurve.EaseOut;
        Assert.Equal(0f, curve.Evaluate(0f), 6);
        Assert.Equal(0.75f, curve.Evaluate(0.5f), 6);
        Assert.Equal(1f, curve.Evaluate(1f), 6);
    }

    [Fact]
    public void EaseInOut_IsSmoothstep()
    {
        var curve = ParticleCurve.EaseInOut;
        Assert.Equal(0f, curve.Evaluate(0f), 6);
        Assert.Equal(0.5f, curve.Evaluate(0.5f), 6);
        Assert.Equal(1f, curve.Evaluate(1f), 6);
        Assert.Equal(S(0.3f), curve.Evaluate(0.3f), 6);
    }

    [Fact]
    public void Flash_SitsAtEnd_HitsStart_ReturnsToEnd()
    {
        var curve = ParticleCurve.Flash(0.15f);
        Assert.Equal(1f, curve.Evaluate(0f), 6);   // End at birth
        Assert.Equal(0f, curve.Evaluate(0.15f), 6); // Start at the peak
        Assert.Equal(1f, curve.Evaluate(1f), 6);   // End at death
    }

    [Fact]
    public void Flash_DefaultParam_UsedWhenNonPositive()
    {
        var explicitPeak = ParticleCurve.Flash(0.15f);
        var zeroParam = new ParticleCurve(ParticleCurveKind.Flash, 0f);
        for (float n = 0f; n <= 1f; n += 0.1f)
        {
            Assert.Equal(explicitPeak.Evaluate(n), zeroParam.Evaluate(n), 6);
        }
    }

    [Fact]
    public void FadeInOut_OneAtEdges_ZeroAcrossPlateau()
    {
        // Remap sits at 1 at birth and death (End) and falls to 0 across the middle (Start), so a
        // transparent End plus a visible Start reads as fade-in, hold, fade-out.
        var curve = ParticleCurve.FadeInOut(0.2f);
        Assert.Equal(1f, curve.Evaluate(0f), 6);
        Assert.Equal(0.5f, curve.Evaluate(0.1f), 6); // halfway through the ramp
        Assert.Equal(0f, curve.Evaluate(0.2f), 6);   // fully at Start once the edge fraction is cleared
        Assert.Equal(0f, curve.Evaluate(0.5f), 6);   // held across the plateau
        Assert.Equal(0f, curve.Evaluate(0.8f), 6);
        Assert.Equal(0.5f, curve.Evaluate(0.9f), 6);
        Assert.Equal(1f, curve.Evaluate(1f), 6);
    }

    [Fact]
    public void FadeInOut_DefaultParam_UsedWhenNonPositive()
    {
        var explicitEdge = ParticleCurve.FadeInOut(0.2f);
        var zeroParam = new ParticleCurve(ParticleCurveKind.FadeInOut, 0f);
        for (float n = 0f; n <= 1f; n += 0.1f)
        {
            Assert.Equal(explicitEdge.Evaluate(n), zeroParam.Evaluate(n), 6);
        }
    }

    [Fact]
    public void Pulse_StartsAndEndsAtZero_PeaksBetween()
    {
        var curve = ParticleCurve.Pulse(2f);
        Assert.Equal(0f, curve.Evaluate(0f), 6);
        // 2 cycles over [0,1]: a full period every 0.5, back to 0 at 0.5 and 1.0.
        Assert.Equal(0f, curve.Evaluate(0.5f), 6);
        Assert.Equal(0f, curve.Evaluate(1f), 6);
        // Peaks at the quarter-cycle points.
        Assert.Equal(1f, curve.Evaluate(0.25f), 6);
    }

    [Fact]
    public void Pulse_DefaultParam_UsedWhenNonPositive()
    {
        var explicitCycles = ParticleCurve.Pulse(2f);
        var zeroParam = new ParticleCurve(ParticleCurveKind.Pulse, 0f);
        for (float n = 0f; n <= 1f; n += 0.1f)
        {
            Assert.Equal(explicitCycles.Evaluate(n), zeroParam.Evaluate(n), 6);
        }
    }

    [Fact]
    public void AllKinds_StayWithinUnitRange()
    {
        ParticleCurve[] curves =
        {
            ParticleCurve.Linear,
            ParticleCurve.EaseIn,
            ParticleCurve.EaseOut,
            ParticleCurve.EaseInOut,
            ParticleCurve.Flash(),
            ParticleCurve.FadeInOut(),
            ParticleCurve.Pulse(),
        };

        foreach (var curve in curves)
        {
            for (float n = 0f; n <= 1f; n += 0.02f)
            {
                float v = curve.Evaluate(n);
                Assert.True(v >= -1e-4f && v <= 1f + 1e-4f, $"{curve.Kind} at {n} => {v}");
            }
        }
    }
}
