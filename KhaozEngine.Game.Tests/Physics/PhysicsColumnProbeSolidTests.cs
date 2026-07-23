using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

/// <summary>
/// <see cref="PhysicsColumnProbe"/> against the REAL <see cref="BepuPhysicsWorld"/> (headless, no GPU),
/// the case the scripted fake world in <c>PhysicsColumnProbeTests</c> cannot exhibit: a downward ray whose
/// origin lies inside a solid convex. Bepu reports an inside-origin hit at t == 0 on the cast origin, so
/// the old sweep re-hit the same solid every centimetre and stacked a run of phantom surfaces through its
/// interior (issue #273). These pin the fixed contract - one standable surface per exposed top face, real
/// headrooms, no phantom stack - across box, stacked boxes, a solid deck over ground, and a tall solid.
/// </summary>
public class PhysicsColumnProbeSolidTests
{
    static BepuPhysicsWorld SettledWorld()
    {
        var w = new BepuPhysicsWorld();
        return w;
    }

    // A solid box static from its centre and half-extents.
    static void AddBox(BepuPhysicsWorld w, Vector3 center, Vector3 halfExtents)
        => w.AddStatic(new BoxShape(halfExtents), new Pose(center, Quaternion.Identity));

    [Fact]
    public void SingleSolidBox_YieldsOnlyItsTop_NoPhantomStack()
    {
        // The issue's exact repro: a 0.30 m-thick box spanning y 2.869 .. 3.169. Before the fix this
        // returned 16 phantom surfaces ~1 cm apart; the top is the only real standable surface.
        using var w = SettledWorld();
        AddBox(w, new Vector3(20f, 3.019f, 20f), new Vector3(1f, 0.15f, 1f));
        w.Step(1f / 60f);

        var probe = new PhysicsColumnProbe(w);
        Span<ColumnSurface> hits = stackalloc ColumnSurface[8];
        int n = probe.Sample(20f, 20f, hits);

        Assert.Equal(1, n);
        Assert.Equal(3.169f, hits[0].Height, 3);
        Assert.True(float.IsPositiveInfinity(hits[0].Headroom));
    }

    [Fact]
    public void TwoStackedSolidBoxes_WithGap_TwoSurfaces_HeadroomToUnderside()
    {
        // Upper box spans 3.0 .. 3.2 (top 3.2), lower box spans 2.0 .. 2.4 (top 2.4). Gap 0.6 between the
        // upper underside (3.0) and the lower top (2.4). Two standable tops; the lower top's headroom is
        // measured to the upper box's underside, not to its far-off top.
        using var w = SettledWorld();
        AddBox(w, new Vector3(5f, 3.1f, 5f), new Vector3(0.5f, 0.1f, 0.5f));  // 3.0 .. 3.2
        AddBox(w, new Vector3(5f, 2.2f, 5f), new Vector3(0.5f, 0.2f, 0.5f));  // 2.0 .. 2.4
        w.Step(1f / 60f);

        var probe = new PhysicsColumnProbe(w);
        Span<ColumnSurface> hits = stackalloc ColumnSurface[8];
        int n = probe.Sample(5f, 5f, hits);

        Assert.Equal(2, n);
        // Ascending: lower top first.
        Assert.Equal(2.4f, hits[0].Height, 3);
        Assert.InRange(hits[0].Headroom, 0.58f, 0.62f);      // ~0.6 to the upper underside (3.0)
        Assert.Equal(3.2f, hits[1].Height, 3);
        Assert.True(float.IsPositiveInfinity(hits[1].Headroom));
    }

    [Fact]
    public void SolidDeckOverGround_DeckUndersideIsCeilingOnly_NotAStandableSurface()
    {
        // The bridge scenario from the fake-world tests, now with a REAL solid deck: a thin deck box
        // spanning 1.8 .. 2.0 sitting above a ground slab whose top is 0. Two standable surfaces (ground
        // and deck top); the deck's underside (1.8) is a ceiling only - it bounds the ground's headroom
        // and never appears as its own surface.
        using var w = SettledWorld();
        AddBox(w, new Vector3(0f, -0.5f, 0f), new Vector3(4f, 0.5f, 4f));   // ground slab, top at 0
        AddBox(w, new Vector3(0f, 1.9f, 0f), new Vector3(1f, 0.1f, 1f));    // deck 1.8 .. 2.0
        w.Step(1f / 60f);

        var probe = new PhysicsColumnProbe(w);
        Span<ColumnSurface> hits = stackalloc ColumnSurface[8];
        int n = probe.Sample(0f, 0f, hits);

        Assert.Equal(2, n);
        // Ascending: ground first, its headroom bounded by the deck underside (~1.8).
        Assert.Equal(0f, hits[0].Height, 3);
        Assert.InRange(hits[0].Headroom, 1.78f, 1.82f);
        // Deck top, open sky above.
        Assert.Equal(2.0f, hits[1].Height, 3);
        Assert.True(float.IsPositiveInfinity(hits[1].Headroom));
    }

    [Fact]
    public void TallSolid_SingleTopSurface_SurvivesTinyBuffer()
    {
        // A 10 m-tall solid (top at 10). With the bug, its interior stacked ~1000 phantom surfaces and the
        // "keep lowest on overflow" rule evicted the real top, so a small buffer baked no navigable top at
        // all. A buffer of ONE must now still hold the real top and nothing else.
        using var w = SettledWorld();
        AddBox(w, new Vector3(-30f, 5f, -30f), new Vector3(1f, 5f, 1f));    // 0 .. 10
        w.Step(1f / 60f);

        var probe = new PhysicsColumnProbe(w);
        Span<ColumnSurface> hits = stackalloc ColumnSurface[1];
        int n = probe.Sample(-30f, -30f, hits);

        Assert.Equal(1, n);
        Assert.Equal(10f, hits[0].Height, 3);
        Assert.True(float.IsPositiveInfinity(hits[0].Headroom));
    }
}
