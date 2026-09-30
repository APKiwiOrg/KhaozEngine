using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Render3D;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>
/// Ray picking against object MODELS: the box tested is the box drawn, at the drawn transform, so a well's
/// roof counts as the well. The bounds are hand-built per test, because what is under test is the picking and
/// not the measuring (<see cref="TileObjectBoundsCacheTests"/> owns that half).
/// </summary>
public class TileObjectRaycastTests
{
    static TileWorldCatalogs Catalogs => TileRenderTestData.Catalogs;

    static TileWorldDocument World() => TileRenderTestData.GreyboxWorld();

    // A tall well-shaped box for every archetype asked about: 1.2 metres wide, 3 metres high, centred on the
    // anchor. Tall on purpose, so an oblique ray can cross the model high above the ground tile it stands on.
    static bool TallBox(TileObjectArchetype archetype, out Vector3 min, out Vector3 max)
    {
        min = new Vector3(-0.6f, 0f, -0.6f);
        max = new Vector3(0.6f, 3f, 0.6f);
        return true;
    }

    [Fact]
    public void An_oblique_ray_through_the_models_upper_half_names_the_object()
    {
        TileWorldDocument doc = World();
        TileObject well = doc.AddObject("wall", 40, 40, 0, 0);
        TileObjectArchetype archetype = Catalogs.Archetype("wall")!;
        Vector3 at = TileObjectProps.AnchorPosition(doc, archetype, well);

        // Enters the box at about 2.5 metres up, from far away and to the side: the ground tile this ray
        // eventually strikes is well behind the object, which is exactly the roof click the footprint join
        // could never answer.
        Vector3 target = at + new Vector3(0f, 2.5f, 0f);
        var origin = new Vector3(at.X + 30f, 12f, at.Z + 30f);
        var hits = new List<TileObjectHit>();

        Assert.Equal(1, TileObjectRaycast.Pick(doc, Catalogs, 0, origin, target - origin, 600f, TallBox, hits));
        Assert.Equal(well.Id, hits[0].ObjectId);
        Assert.Equal("wall", hits[0].ArchetypeId);
        Assert.True(hits[0].Distance > 0f);

        // And a ray past the model's top misses: the box is the whole answer, not the footprint under it.
        Vector3 above = at + new Vector3(0f, 4.5f, 0f);
        Assert.Equal(0, TileObjectRaycast.Pick(doc, Catalogs, 0, origin, above - origin, 600f, TallBox, hits));
    }

    [Fact]
    public void Hits_order_nearest_first_and_an_exact_tie_takes_the_lower_id()
    {
        TileWorldDocument doc = World();
        TileObject near = doc.AddObject("wall", 40, 40, 0, 0);
        TileObject far = doc.AddObject("wall", 40, 44, 0, 0);
        TileObjectArchetype archetype = Catalogs.Archetype("wall")!;
        Vector3 nearAt = TileObjectProps.AnchorPosition(doc, archetype, near);
        Vector3 farAt = TileObjectProps.AnchorPosition(doc, archetype, far);

        // Down the line the two stand on, crossing both boxes at half height.
        var origin = new Vector3(nearAt.X, 1.5f, nearAt.Z + 20f);
        Vector3 direction = new Vector3(farAt.X, 1.5f, farAt.Z - 20f) - origin;
        var hits = new List<TileObjectHit>();

        Assert.Equal(2, TileObjectRaycast.Pick(doc, Catalogs, 0, origin, direction, 600f, TallBox, hits));
        Assert.Equal(near.Id, hits[0].ObjectId);
        Assert.Equal(far.Id, hits[1].ObjectId);
        Assert.True(hits[0].Distance < hits[1].Distance);

        // Two objects on ONE tile are one box twice: identical entry distances, and the list still answers
        // the same way on every run because the tie goes to the lower id.
        TileWorldDocument stacked = World();
        TileObject second = stacked.AddObject("wall", 40, 40, 0, 0);
        TileObject first = stacked.AddObject("wall", 40, 40, 0, 0);
        Assert.True(second.Id < first.Id);
        Assert.Equal(2, TileObjectRaycast.Pick(stacked, Catalogs, 0, origin, direction, 600f, TallBox, hits));
        Assert.Equal(second.Id, hits[0].ObjectId);
    }

    [Fact]
    public void The_box_turns_with_the_drawn_rotation()
    {
        TileWorldDocument doc = World();
        TileObject turned = doc.AddObject("wall", 40, 40, 0, 1);
        TileObjectArchetype archetype = Catalogs.Archetype("wall")!;
        Vector3 at = TileObjectProps.AnchorPosition(doc, archetype, turned);
        float yaw = TileObjectProps.YawRadians(archetype, turned.Rotation);

        // A front-only snout box, nothing behind the anchor, so a flipped rotation sign cannot hide behind
        // symmetry (the actor clickbox review's lesson). The world-space probe points are the DRAW transform
        // applied to a local point inside and a local point mirrored outside, so this pins picker == draw
        // whatever the yaw convention's sign is.
        static bool Snout(TileObjectArchetype a, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(-0.2f, 0f, 0.3f);
            max = new Vector3(0.2f, 1f, 0.9f);
            return true;
        }
        Vector3 inside = at + Vector3.Transform(new Vector3(0f, 0.5f, 0.6f), Matrix4x4.CreateRotationY(yaw));
        Vector3 outside = at + Vector3.Transform(new Vector3(0f, 0.5f, -0.6f), Matrix4x4.CreateRotationY(yaw));
        var hits = new List<TileObjectHit>();

        Assert.Equal(1, TileObjectRaycast.Pick(doc, Catalogs, 0,
            inside with { Y = 20f }, -Vector3.UnitY * 40f, 600f, Snout, hits));
        Assert.Equal(turned.Id, hits[0].ObjectId);
        Assert.Equal(0, TileObjectRaycast.Pick(doc, Catalogs, 0,
            outside with { Y = 20f }, -Vector3.UnitY * 40f, 600f, Snout, hits));
    }

    [Fact]
    public void The_cut_and_the_skips_hold()
    {
        TileWorldDocument doc = World();
        TileObject well = doc.AddObject("wall", 40, 40, 0, 0);
        TileObjectArchetype archetype = Catalogs.Archetype("wall")!;
        Vector3 at = TileObjectProps.AnchorPosition(doc, archetype, well);
        var origin = new Vector3(at.X, 1.5f, at.Z + 20f);
        Vector3 direction = new Vector3(0f, 0f, -1f);
        var hits = new List<TileObjectHit>();

        // In unit-direction units the box is about 19.4 away: a cut short of it drops the hit.
        Assert.Equal(1, TileObjectRaycast.Pick(doc, Catalogs, 0, origin, direction, 600f, TallBox, hits));
        Assert.Equal(0, TileObjectRaycast.Pick(doc, Catalogs, 0, origin, direction, 10f, TallBox, hits));

        // A bounds source with no box for the archetype drops the object rather than inventing one.
        static bool None(TileObjectArchetype a, out Vector3 min, out Vector3 max)
        {
            min = default;
            max = default;
            return false;
        }
        Assert.Equal(0, TileObjectRaycast.Pick(doc, Catalogs, 0, origin, direction, 600f, None, hits));
    }

    [Fact]
    public void A_plane_pick_crosses_negative_empty_and_missing_regions_in_nearest_order()
    {
        var doc = new TileWorldDocument();
        doc.GetOrCreateRegion(new RegionCoord(-1, -1));
        doc.GetOrCreateRegion(new RegionCoord(0, -1)); // Empty, with region (1, -1) missing beyond it.
        doc.GetOrCreateRegion(new RegionCoord(2, -1));
        TileObject far = doc.AddObject("wall", 128, -1, 0, 0);
        TileObject near = doc.AddObject("wall", -1, -1, 0, 0);
        TileObject upstairs = doc.AddObject("wall", -1, -1, 1, 0);
        static bool AcrossPlanes(TileObjectArchetype a, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(-0.5f, -4f, -0.5f);
            max = new Vector3(0.5f, 4f, 0.5f);
            return true;
        }
        var origin = new Vector3(-10f, 1.5f, 0.5f);
        var hits = new List<TileObjectHit>();

        Assert.Equal(2, TileObjectRaycast.Pick(doc, Catalogs, 0, origin, Vector3.UnitX, 160f, AcrossPlanes, hits));
        Assert.Equal(near.Id, hits[0].ObjectId);
        Assert.Equal(9f, hits[0].Distance);
        Assert.Equal(far.Id, hits[1].ObjectId);
        Assert.Equal(138f, hits[1].Distance);
        Assert.Equal(1, TileObjectRaycast.Pick(doc, Catalogs, 1, origin, Vector3.UnitX, 160f, AcrossPlanes, hits));
        Assert.Equal(upstairs.Id, hits[0].ObjectId);
        Assert.Equal(0, TileObjectRaycast.Pick(doc, Catalogs, 2, origin, Vector3.UnitX, 160f, AcrossPlanes, hits));
        Assert.Equal(3, doc.Regions.Count);
    }

    [Fact]
    public void Only_anchors_inside_the_padded_rect_are_candidates_even_for_a_wide_box()
    {
        var doc = new TileWorldDocument();
        foreach (RegionCoord region in new[] { new RegionCoord(-1, -1), new RegionCoord(0, 0) })
            doc.GetOrCreateRegion(region);
        TileObject low = doc.AddObject("wall", -8, -8, 0, 0);
        TileObject high = doc.AddObject("wall", 8, 8, 0, 0);
        doc.AddObject("wall", -9, -8, 0, 0);
        doc.AddObject("wall", 9, 8, 0, 0);
        doc.AddObject("wall", -8, -9, 0, 0);
        doc.AddObject("wall", 8, 9, 0, 0);
        static bool WideBox(TileObjectArchetype a, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(-100f, 0f, -100f);
            max = new Vector3(100f, 1f, 100f);
            return true;
        }
        var hits = new List<TileObjectHit>();

        // A vertical ray at tile (0, 0) searches [-8, 9) on both axes, including the padded corners.
        Assert.Equal(2, TileObjectRaycast.Pick(doc, Catalogs, 0, new Vector3(0f, 10f, 0f),
            -Vector3.UnitY, 20f, WideBox, hits));
        Assert.Equal(low.Id, hits[0].ObjectId);
        Assert.Equal(high.Id, hits[1].ObjectId);
        Assert.Equal(0, TileObjectRaycast.Pick(doc, Catalogs, 0, new Vector3(0f, 10f, 0f),
            -Vector3.UnitY, 9f, WideBox, hits)); // Entry at the maximum distance stays excluded.
    }
}
