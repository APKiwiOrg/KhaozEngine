using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D.Vfx;
using Xunit;

namespace KhaozEngine.Tests.Render2D.Vfx;

/// <summary>Headless coverage for the pure alpha / scale / offset curves behind floating world text.</summary>
public class FloatingTextCurvesTests
{
    const float Tol = 1e-4f;

    static FloatingTextStyle Style => new()
    {
        Color = Color.White,
        LifetimeSeconds = 2f,
        DriftPerSecond = new Vector2(-30f, -40f),
        StartScale = 1f,
        EndScale = 2f,
        FadeInSeconds = 0.5f,
        FadeOutSeconds = 0.5f,
        StackSpacing = 10f,
    };

    [Fact]
    public void Default_HasSensiblePreset()
    {
        FloatingTextStyle p = FloatingTextStyle.Default;
        Assert.Equal(Color.White, p.Color);
        Assert.Equal(1.5f, p.LifetimeSeconds, Tol);
        Assert.Equal(new Vector2(0f, -40f), p.DriftPerSecond);
        Assert.Equal(1f, p.StartScale, Tol);
        Assert.Equal(1f, p.EndScale, Tol);
        Assert.Equal(0.1f, p.FadeInSeconds, Tol);
        Assert.Equal(0.5f, p.FadeOutSeconds, Tol);
        Assert.Equal(4, p.MaxPerAnchor);
        Assert.Equal(14f, p.StackSpacing, Tol);
        Assert.True(p.Shadow);
    }

    // A record struct's bare new() is all-zero, which is a zero lifetime, which must draw nothing at any age rather
    // than a white line at the origin. Same no-op default AttentionBeaconParams has.
    [Fact]
    public void BareNew_IsInvisibleAtEveryAge()
    {
        var p = new FloatingTextStyle();
        Assert.Equal(0f, FloatingTextCurves.AlphaAt(0f, p), Tol);
        Assert.Equal(0f, FloatingTextCurves.AlphaAt(1f, p), Tol);
    }

    [Fact]
    public void AlphaAt_FadesInThenHoldsThenFadesOut()
    {
        FloatingTextStyle p = Style;
        Assert.Equal(0f, FloatingTextCurves.AlphaAt(0f, p), Tol);       // birth
        Assert.Equal(0.5f, FloatingTextCurves.AlphaAt(0.25f, p), Tol);  // half way in
        Assert.Equal(1f, FloatingTextCurves.AlphaAt(0.5f, p), Tol);     // fade-in done
        Assert.Equal(1f, FloatingTextCurves.AlphaAt(1f, p), Tol);       // mid, holding
        Assert.Equal(0.5f, FloatingTextCurves.AlphaAt(1.75f, p), Tol);  // half way out
        Assert.Equal(0f, FloatingTextCurves.AlphaAt(2f, p), Tol);       // dead on the lifetime
        Assert.Equal(0f, FloatingTextCurves.AlphaAt(9f, p), Tol);       // and after it
        Assert.Equal(0f, FloatingTextCurves.AlphaAt(-1f, p), Tol);      // and before it
    }

    // The two fades overlapping must not let either be exceeded: the smaller wins, so a style with a one second
    // fade-in and a one second fade-out over a one second life peaks half way rather than reaching 1.
    [Fact]
    public void AlphaAt_TakesTheSmallerOfTwoOverlappingFades()
    {
        FloatingTextStyle p = Style with { LifetimeSeconds = 1f, FadeInSeconds = 1f, FadeOutSeconds = 1f };
        Assert.Equal(0.5f, FloatingTextCurves.AlphaAt(0.5f, p), Tol);
        Assert.Equal(0.25f, FloatingTextCurves.AlphaAt(0.25f, p), Tol);
        Assert.Equal(0.25f, FloatingTextCurves.AlphaAt(0.75f, p), Tol);
    }

    [Fact]
    public void ScaleAt_LerpsAcrossTheLifetimeAndClampsBothEnds()
    {
        FloatingTextStyle p = Style;
        Assert.Equal(1f, FloatingTextCurves.ScaleAt(0f, p), Tol);
        Assert.Equal(1.5f, FloatingTextCurves.ScaleAt(1f, p), Tol);
        Assert.Equal(2f, FloatingTextCurves.ScaleAt(2f, p), Tol);
        Assert.Equal(2f, FloatingTextCurves.ScaleAt(20f, p), Tol);   // clamped, not still growing
        Assert.Equal(1f, FloatingTextCurves.ScaleAt(-5f, p), Tol);   // clamped the other way
        // No span to travel: the start scale is all there is to answer.
        Assert.Equal(1f, FloatingTextCurves.ScaleAt(3f, p with { LifetimeSeconds = 0f }), Tol);
    }

    [Fact]
    public void OffsetAt_IsDriftIntegratedPlusTheStackStep()
    {
        FloatingTextStyle p = Style;
        Assert.Equal(Vector2.Zero, FloatingTextCurves.OffsetAt(0f, p, 0));
        Assert.Equal(new Vector2(-15f, -20f), FloatingTextCurves.OffsetAt(0.5f, p, 0));
        Assert.Equal(new Vector2(-30f, -40f), FloatingTextCurves.OffsetAt(1f, p, 0));
        // A negative age is read as zero rather than driving the text backwards.
        Assert.Equal(Vector2.Zero, FloatingTextCurves.OffsetAt(-2f, p, 0));
    }

    // The step is DOWN the screen and constant in age, so the oldest of a burst (index 0) sits highest and the
    // column never changes shape as it drifts.
    [Fact]
    public void OffsetAt_StacksNewerEntriesBelowOlderOnes()
    {
        FloatingTextStyle p = Style;
        Assert.Equal(new Vector2(0f, 10f), FloatingTextCurves.OffsetAt(0f, p, 1));
        Assert.Equal(new Vector2(0f, 30f), FloatingTextCurves.OffsetAt(0f, p, 3));

        Vector2 oldest = FloatingTextCurves.OffsetAt(1f, p, 0);
        Vector2 newer = FloatingTextCurves.OffsetAt(1f, p, 2);
        Assert.True(newer.Y > oldest.Y, "the newer entry of a burst sits below the older one");
        Assert.Equal(oldest.Y + 20f, newer.Y, Tol);
        Assert.Equal(oldest.X, newer.X, Tol);
    }
}
