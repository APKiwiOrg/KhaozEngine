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

    // Distinct solid columns, including their original headroom bands and full box geometry.
    public static TheoryData<string, Vector3, Vector3, Vector3, Vector3, float, float, float, float> StackedSolids => new()
    {
        {
            "stacked boxes", new(5f, 3.1f, 5f), new(0.5f, 0.1f, 0.5f),
            new(5f, 2.2f, 5f), new(0.5f, 0.2f, 0.5f), 2.4f, 3.2f, 0.58f, 0.62f
        },
        {
            "deck over ground", new(0f, -0.5f, 0f), new(4f, 0.5f, 4f),
            new(0f, 1.9f, 0f), new(1f, 0.1f, 1f), 0f, 2f, 1.78f, 1.82f
        },
    };

    [Theory]
    [MemberData(nameof(StackedSolids))]
    public void StackedSolids_ExposeOnlyTheirTops_WithHeadroomToTheUnderside(string scenario,
        Vector3 firstCenter, Vector3 firstHalf, Vector3 secondCenter, Vector3 secondHalf,
        float lowerTop, float upperTop, float minimumHeadroom, float maximumHeadroom)
    {
        using var w = SettledWorld();
        AddBox(w, firstCenter, firstHalf);
        AddBox(w, secondCenter, secondHalf);
        w.Step(1f / 60f);

        var probe = new PhysicsColumnProbe(w);
        Span<ColumnSurface> hits = stackalloc ColumnSurface[8];
        int n = probe.Sample(firstCenter.X, firstCenter.Z, hits);

        Assert.True(n == 2, $"{scenario}: expected two exposed top surfaces, got {n}");
        Assert.Equal(lowerTop, hits[0].Height, 3);
        Assert.InRange(hits[0].Headroom, minimumHeadroom, maximumHeadroom);
        Assert.Equal(upperTop, hits[1].Height, 3);
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
