using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

// Regression: a ConvexHullShape static must be BASE-aligned to its pose, not sunk by its centroid.
// BepuPhysics recenters a ConvexHull on its centre of mass, so a hull added at Pose.At(p) was placed with
// its CENTROID at p, sinking a base-at-y=0 prop collider ~centroid-height into the ground (the character
// then sank into rocks). The backend wraps the hull in a base-aligning compound; these tests pin that.
public class ConvexHullAlignmentTests
{
    // A tall, off-centre-peaked hull: base at y=0, peak ~y=2 at (0.6, 2, 0). Centroid is well above y=0,
    // so a naive (unwrapped) hull would sit clearly below where the points say it should be.
    static ConvexHullShape TallHull() => new(new[]
    {
        new Vector3( 1.0f, 0f,  1.0f), new Vector3(-1.0f, 0f,  1.0f),
        new Vector3( 1.0f, 0f, -1.0f), new Vector3(-1.0f, 0f, -1.0f),
        new Vector3( 0.7f, 1.0f, 0.7f), new Vector3(-0.7f, 1.0f, 0.7f),
        new Vector3( 0.7f, 1.0f,-0.7f), new Vector3(-0.7f, 1.0f,-0.7f),
        new Vector3( 0.6f, 2.0f, 0.0f),   // peak, off-centre in X
    });

    [Fact]
    public void ConvexHull_StaticIsBaseAligned_TopMatchesPoints()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(TallHull(), Pose.At(Vector3.Zero));
        world.Step(1f / 60f);

        // Ray straight down over the peak XZ: the hull top there must be near the point's y=2 (base-aligned),
        // NOT ~2 minus the centroid height (which would be ~1.2-1.4 if the hull were centroid-placed).
        bool hit = world.Raycast(new Vector3(0.6f, 5f, 0f), -Vector3.UnitY, 10f, out RayHit rh);
        Assert.True(hit, "ray must hit the hull");
        float topAtPeak = 5f - rh.Distance;
        Assert.True(topAtPeak > 1.85f, $"hull top over the peak must be ~2 (base-aligned), was {topAtPeak:F3}");

        // And the base must sit at ~y=0 (a downward ray just inside the footprint reaches near the base
        // before passing through; check the hull does not float by confirming a low point near the base).
        bool baseHit = world.Raycast(new Vector3(0.9f, 5f, 0.9f), -Vector3.UnitY, 10f, out RayHit rb);
        Assert.True(baseHit, "ray near the footprint corner must hit the hull");
        float topAtCorner = 5f - rb.Distance;
        Assert.True(topAtCorner < 0.6f, $"hull near the base corner must be low (~0), was {topAtCorner:F3}");
    }

    [Fact]
    public void ConvexHull_RespectsPoseTranslation()
    {
        // Place the same hull lifted by +3 in Y; the base-aligned top over the peak should follow to ~5.
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(TallHull(), Pose.At(new Vector3(0f, 3f, 0f)));
        world.Step(1f / 60f);
        bool hit = world.Raycast(new Vector3(0.6f, 9f, 0f), -Vector3.UnitY, 12f, out RayHit rh);
        Assert.True(hit);
        float top = 9f - rh.Distance;
        Assert.True(top > 4.85f && top < 5.15f, $"lifted hull top must be ~5 (base 3 + peak 2), was {top:F3}");
    }
}
