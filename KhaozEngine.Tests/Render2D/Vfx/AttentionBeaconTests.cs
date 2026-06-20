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
}
