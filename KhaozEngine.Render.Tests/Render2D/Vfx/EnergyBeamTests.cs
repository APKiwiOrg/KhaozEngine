using System;
using System.Numerics;
using KhaozEngine.Render2D.Vfx;
using Xunit;

namespace KhaozEngine.Tests.Render2D.Vfx;

/// <summary>Headless coverage for the pure geometry/animation helpers behind the energy beam renderer.</summary>
public class EnergyBeamTests
{
    const float Tol = 1e-4f;

    [Fact]
    public void Axis_HorizontalBeam_HasLengthAndZeroAngle()
    {
        var (length, angle) = EnergyBeam.Axis(new Vector2(0, 0), new Vector2(10, 0));
        Assert.Equal(10f, length, Tol);
        Assert.Equal(0f, angle, Tol);
    }

    [Fact]
    public void Axis_VerticalBeam_HasRightAngle()
    {
        var (length, angle) = EnergyBeam.Axis(new Vector2(0, 0), new Vector2(0, 5));
        Assert.Equal(5f, length, Tol);
        Assert.Equal(MathF.PI / 2f, angle, Tol);
    }

    [Fact]
    public void Perpendicular_OfHorizontalBeam_IsUnitVertical()
    {
        Vector2 perp = EnergyBeam.Perpendicular(new Vector2(0, 0), new Vector2(10, 0));
        Assert.Equal(0f, perp.X, Tol);
        Assert.Equal(1f, perp.Y, Tol);
        Assert.Equal(1f, perp.Length(), Tol);
    }

    [Fact]
    public void Perpendicular_OfDegenerateBeam_IsZero()
    {
        Vector2 perp = EnergyBeam.Perpendicular(new Vector2(3, 3), new Vector2(3, 3));
        Assert.Equal(0f, perp.Length(), Tol);
    }

    [Fact]
    public void DashAlpha_ZeroDashLength_IsSolid()
    {
        Assert.Equal(1f, EnergyBeam.DashAlpha(0f, 0f, dashLength: 0f, dashGap: 5f, dashSpeed: 1f), Tol);
        Assert.Equal(1f, EnergyBeam.DashAlpha(123f, 7f, dashLength: 0f, dashGap: 5f, dashSpeed: 1f), Tol);
    }

    [Fact]
    public void DashAlpha_LitInsideDash_DarkInGap()
    {
        // period = 4: [0,2) lit, [2,4) gap. No flow (speed 0).
        Assert.Equal(1f, EnergyBeam.DashAlpha(0f, 0f, 2f, 2f, 0f), Tol);
        Assert.Equal(1f, EnergyBeam.DashAlpha(1.9f, 0f, 2f, 2f, 0f), Tol);
        Assert.Equal(0f, EnergyBeam.DashAlpha(2.1f, 0f, 2f, 2f, 0f), Tol);
        Assert.Equal(0f, EnergyBeam.DashAlpha(3.9f, 0f, 2f, 2f, 0f), Tol);
        Assert.Equal(1f, EnergyBeam.DashAlpha(4.1f, 0f, 2f, 2f, 0f), Tol);
    }

    [Fact]
    public void DashAlpha_FlowsWithTime()
    {
        // speed 1, time 1 shifts the pattern back by 1 unit: distance 0 now sits in the gap.
        Assert.Equal(0f, EnergyBeam.DashAlpha(0f, 1f, 2f, 2f, 1f), Tol);
        Assert.Equal(1f, EnergyBeam.DashAlpha(1f, 1f, 2f, 2f, 1f), Tol);
    }

    [Fact]
    public void DefaultBeamParams_HasSquareEnds()
    {
        // Existing callers must be unchanged: the default cap mode draws no end-caps (square ends).
        Assert.Equal(BeamCap.None, BeamParams.Default.Caps);
    }

    [Fact]
    public void RoundCaps_DisabledCap_EmitsNoCap()
    {
        var caps = EnergyBeam.RoundCaps(new Vector2(0, 0), new Vector2(10, 0), BeamCap.None, bandWidth: 8f, pulse: 1f);
        Assert.False(caps.Enabled);
    }

    [Fact]
    public void RoundCaps_Enabled_EmitsDiscAtEachEndSizedToHalfWidth()
    {
        Vector2 a = new(0, 0), b = new(10, 0);
        var caps = EnergyBeam.RoundCaps(a, b, BeamCap.Round, bandWidth: 8f, pulse: 1f);

        Assert.True(caps.Enabled);
        Assert.Equal(a, caps.A);
        Assert.Equal(b, caps.B);
        Assert.Equal(4f, caps.Radius, Tol); // half the band width
    }

    [Fact]
    public void RoundCaps_ScalesWithPulse()
    {
        var caps = EnergyBeam.RoundCaps(new Vector2(0, 0), new Vector2(10, 0), BeamCap.Round, bandWidth: 8f, pulse: 1.5f);
        Assert.Equal(6f, caps.Radius, Tol); // 8 * 1.5 / 2
    }

    [Fact]
    public void RoundCaps_CoreAndGlowGetDifferentlySizedCaps()
    {
        Vector2 a = new(0, 0), b = new(10, 0);
        var glowCaps = EnergyBeam.RoundCaps(a, b, BeamCap.Round, bandWidth: 12f, pulse: 1f);
        var coreCaps = EnergyBeam.RoundCaps(a, b, BeamCap.Round, bandWidth: 3f, pulse: 1f);
        Assert.Equal(6f, glowCaps.Radius, Tol);
        Assert.Equal(1.5f, coreCaps.Radius, Tol);
    }

    [Fact]
    public void RoundCaps_ZeroLengthBeam_EmitsNoCap()
    {
        var caps = EnergyBeam.RoundCaps(new Vector2(3, 3), new Vector2(3, 3), BeamCap.Round, bandWidth: 8f, pulse: 1f);
        Assert.False(caps.Enabled);
    }

    [Fact]
    public void RoundCaps_ZeroWidthBand_EmitsNoCap()
    {
        var caps = EnergyBeam.RoundCaps(new Vector2(0, 0), new Vector2(10, 0), BeamCap.Round, bandWidth: 0f, pulse: 1f);
        Assert.False(caps.Enabled);
    }

    // ---- jagged electric-arc geometry (#239) ---------------------------------------------------
    // The sinusoidal JitterAmount wobble is a WAVY STRAIGHT LINE and cannot express a bolt. Jagged mode
    // displaces each segment boundary by its own signed noise under a mid-span envelope instead.

    [Fact]
    public void JitterShape_DefaultsToWaveSoExistingBeamsAreUnchanged()
    {
        Assert.Equal(0, (int)BeamJitter.Wave);
        Assert.Equal(BeamJitter.Wave, BeamParams.Default.JitterShape);
        Assert.Equal(BeamJitter.Wave, default(BeamParams).JitterShape);
        Assert.Equal(0, BeamParams.Default.JitterSeed);
    }

    [Fact]
    public void BoltEnvelope_PinsBothEndsAndPeaksMidSpan()
    {
        Assert.Equal(0f, EnergyBeam.BoltEnvelope(0, 8), Tol);
        Assert.Equal(0f, EnergyBeam.BoltEnvelope(8, 8), Tol);
        Assert.Equal(1f, EnergyBeam.BoltEnvelope(4, 8), Tol);
        // Symmetric about the midpoint.
        Assert.Equal(EnergyBeam.BoltEnvelope(1, 8), EnergyBeam.BoltEnvelope(7, 8), Tol);
        Assert.Equal(EnergyBeam.BoltEnvelope(3, 8), EnergyBeam.BoltEnvelope(5, 8), Tol);
        // Monotone rising over the first half.
        Assert.True(EnergyBeam.BoltEnvelope(1, 8) < EnergyBeam.BoltEnvelope(2, 8));
        Assert.True(EnergyBeam.BoltEnvelope(2, 8) < EnergyBeam.BoltEnvelope(3, 8));
    }

    [Fact]
    public void BoltEnvelope_DegenerateSegmentCountIsZero()
    {
        Assert.Equal(0f, EnergyBeam.BoltEnvelope(0, 0), Tol);
    }

    [Fact]
    public void BoltOffset_EndpointsAreExactlyOnTheAxis()
    {
        Assert.Equal(0f, EnergyBeam.BoltOffset(seed: 7, roll: 3, index: 0, segs: 12, amount: 40f));
        Assert.Equal(0f, EnergyBeam.BoltOffset(seed: 7, roll: 3, index: 12, segs: 12, amount: 40f));
    }

    [Fact]
    public void BoltOffset_StaysInsideTheEnvelopedAmplitude()
    {
        const float amount = 25f;
        for (int i = 0; i <= 16; i++)
        {
            float o = EnergyBeam.BoltOffset(seed: 11, roll: 2, index: i, segs: 16, amount: amount);
            float bound = amount * EnergyBeam.BoltEnvelope(i, 16);
            Assert.True(MathF.Abs(o) <= bound + Tol, $"segment {i} displaced {o}, bound {bound}");
        }
    }

    [Fact]
    public void BoltOffset_IsPureSoTheSameBoltRedrawsIdentically()
    {
        for (int i = 0; i <= 12; i++)
            Assert.Equal(EnergyBeam.BoltOffset(5, 9, i, 12, 30f), EnergyBeam.BoltOffset(5, 9, i, 12, 30f));
    }

    [Fact]
    public void BoltOffset_IsNotTheCoherentWobble_NeighboursDisagreeAndBothSignsAppear()
    {
        bool positive = false, negative = false, neighboursDiffer = false;
        float previous = EnergyBeam.BoltOffset(3, 0, 1, 24, 20f);
        for (int i = 1; i < 24; i++)
        {
            float o = EnergyBeam.BoltOffset(3, 0, i, 24, 20f);
            if (o > 0.5f) positive = true;
            if (o < -0.5f) negative = true;
            if (i > 1 && MathF.Abs(o - previous) > 1f) neighboursDiffer = true;
            previous = o;
        }
        Assert.True(positive && negative, "a bolt displaces to both sides of the axis");
        Assert.True(neighboursDiffer, "adjacent segments must not track each other like a sine wave");
    }

    [Fact]
    public void BoltOffset_DifferentSeedsAndRollsAreDifferentBolts()
    {
        bool seedDiffers = false, rollDiffers = false;
        for (int i = 1; i < 16; i++)
        {
            if (EnergyBeam.BoltOffset(1, 0, i, 16, 20f) != EnergyBeam.BoltOffset(2, 0, i, 16, 20f)) seedDiffers = true;
            if (EnergyBeam.BoltOffset(1, 0, i, 16, 20f) != EnergyBeam.BoltOffset(1, 1, i, 16, 20f)) rollDiffers = true;
        }
        Assert.True(seedDiffers, "the seed must pick a different bolt");
        Assert.True(rollDiffers, "each roll must re-randomize the bolt");
    }

    [Fact]
    public void RollIndex_AdvancesAtTheReRollRate()
    {
        Assert.Equal(0, EnergyBeam.RollIndex(0f, 10f));
        Assert.Equal(0, EnergyBeam.RollIndex(0.09f, 10f));
        Assert.Equal(1, EnergyBeam.RollIndex(0.11f, 10f));
        Assert.Equal(25, EnergyBeam.RollIndex(2.5f, 10f));
    }

    [Fact]
    public void RollIndex_NonPositiveRateHoldsOneStillBolt()
    {
        Assert.Equal(0, EnergyBeam.RollIndex(0f, 0f));
        Assert.Equal(0, EnergyBeam.RollIndex(9.9f, 0f));
        Assert.Equal(0, EnergyBeam.RollIndex(9.9f, -4f));
    }

    [Fact]
    public void RollIndex_NeverGoesBackwardsAsTimeAdvances()
    {
        int previous = int.MinValue;
        for (int i = 0; i < 200; i++)
        {
            int roll = EnergyBeam.RollIndex(i * 0.017f, 18f);
            Assert.True(roll >= previous, $"roll went backwards at t={i * 0.017f}");
            previous = roll;
        }
    }
}
