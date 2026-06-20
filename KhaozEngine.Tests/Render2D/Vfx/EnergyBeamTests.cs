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
}
