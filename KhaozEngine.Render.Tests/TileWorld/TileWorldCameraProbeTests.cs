using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Render3D;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The camera boom probe over a tile world: drawn terrain, water and walk surfaces stop the boom short
/// by the radius, objects stop it through boxes grown by the radius, the filter and the roof rule decide which
/// objects count, and a box the pivot already sits in never collapses the boom.</summary>
public class TileWorldCameraProbeTests
{
    const float Radius = 0.25f;
    const float Length = 10f;

    // The house's west wall is rotation 0 on tile x HouseMinX, a greybox slab hugging the west edge of its tile.
    static float WestWallEastFace(TileWorldDocument doc) =>
        TileRenderTestData.HouseMinX * doc.TileSize + GreyboxMeshResolver.WallThickness;

    static TileWorldView View(TileWorldDocument doc, out TileObjectRaycast.BoundsSource bounds) =>
        View(doc, TileRenderTestData.Catalogs, out bounds);

    static TileWorldView View(TileWorldDocument doc, TileWorldCatalogs catalogs,
                              out TileObjectRaycast.BoundsSource bounds)
    {
        var resolver = new GreyboxMeshResolver(doc.TileSize, doc.PlaneHeight);
        var view = new TileWorldView(new RecordingTileWorldScene(), doc, catalogs, resolver);
        view.LoadRegion(TileRenderTestData.Region);
        bounds = new TileObjectBoundsCache(resolver).TryGetBounds;
        return view;
    }

    static float Reach(TileWorldView view, TileObjectRaycast.BoundsSource bounds,
                       Func<TileObjectArchetype, bool> blocks, Vector3 origin, Vector3 direction) =>
        new TileWorldCameraProbe(view, bounds, blocks).Reach(origin, direction, Length, Radius);

    [Fact]
    public void Open_ground_gives_the_full_length()
    {
        using TileWorldView view = View(TileRenderTestData.HouseWorld(), out var bounds);

        float reach = Reach(view, bounds, _ => true,
            new Vector3(40.5f, 1.5f, -40.5f), Vector3.Normalize(new Vector3(0f, 0.5f, 1f)));

        Assert.Equal(Length, reach);
    }

    [Fact]
    public void A_boom_below_the_horizon_stops_short_of_the_ground_by_the_radius()
    {
        using TileWorldView view = View(TileRenderTestData.HouseWorld(), out var bounds);

        float reach = Reach(view, bounds, _ => true,
            new Vector3(40.5f, 1.5f, -40.5f), new Vector3(0f, -MathF.Sin(1f), MathF.Cos(1f)));

        Assert.Equal(1.5f / MathF.Sin(1f) - Radius, reach, 1e-3f);
    }

    [Fact]
    public void A_pivot_on_flat_ground_with_a_rising_boom_gets_the_full_length()
    {
        using TileWorldView view = View(TileRenderTestData.HouseWorld(), out var bounds);

        float reach = Reach(view, bounds, _ => true,
            new Vector3(40.5f, 0f, -40.5f), Vector3.Normalize(new Vector3(0f, 0.5f, 1f)));

        Assert.Equal(Length, reach);
    }

    [Fact]
    public void A_pivot_on_flat_ground_with_a_falling_boom_is_blocked()
    {
        using TileWorldView view = View(TileRenderTestData.HouseWorld(), out var bounds);

        float reach = Reach(view, bounds, _ => true,
            new Vector3(40.5f, 0f, -40.5f), Vector3.Normalize(new Vector3(0f, -0.5f, 1f)));

        Assert.Equal(0f, reach);
    }

    [Fact]
    public void A_shallow_falling_boom_extends_but_stays_within_a_centimetre_of_the_surface()
    {
        using TileWorldView view = View(TileRenderTestData.HouseWorld(), out var bounds);
        var origin = new Vector3(40.5f, 0f, -40.5f);
        var direction = new Vector3(0f, -MathF.Sin(0.01f), MathF.Cos(0.01f));

        float reach = Reach(view, bounds, _ => true, origin, direction);

        Assert.Equal(0.750017f, reach, 1e-4f);
        Assert.InRange((origin + direction * reach).Y, -0.01f, 0f);
    }

    // The hill's west flank: tile x 19 on rows 20 and 21 is a plane ramp rising 2 m over one tile toward +x, a
    // 63 degree slope. A boom climbing at 72 degrees clears it and the hilltop beyond. Several spots, because
    // whether a pivot on the slope lands a hair above or below it is float rounding and differs per spot.
    [Fact]
    public void A_pivot_on_a_slope_with_a_boom_clearing_the_slope_is_not_collapsed()
    {
        using TileWorldView view = View(TileRenderTestData.HillWorld(), out var bounds);
        Vector3 direction = Vector3.Normalize(new Vector3(1f, 3f, 0f));

        foreach (float x in new[] { 19.1f, 19.3f, 19.5f, 19.7f, 19.9f })
        {
            TileHit ground = Assert.IsType<TileHit>(
                view.PickSurface(0, new Vector3(x, 5f, -21.3f), -Vector3.UnitY, Length));

            float reach = Reach(view, bounds, _ => true, ground.Point, direction);

            Assert.True(reach > 1f, $"pivot {ground.Point} reached {reach}");
        }
    }

    [Fact]
    public void Rising_terrain_shortens_the_reach()
    {
        using TileWorldView view = View(TileRenderTestData.HillWorld(), out var bounds);
        var origin = new Vector3(15.5f, 1f, -21.5f);
        Vector3 direction = Vector3.UnitX;

        float reach = Reach(view, bounds, _ => true, origin, direction);

        TileHit slope = Assert.IsType<TileHit>(view.PickSurface(0, origin, direction, Length));
        Assert.Equal(slope.Distance - Radius, reach, 1e-4f);
        Assert.True(reach < Length);
    }

    [Fact]
    public void A_walk_surface_stops_the_boom()
    {
        TileWorldDocument doc = TileBridgeTestData.World();
        using TileWorldView view = View(doc, TileBridgeTestData.Catalogs, out var bounds);
        var origin = new Vector3(31.5f, 5f, -41.5f);
        Vector3 direction = -Vector3.UnitY;

        // Reject model boxes so only the walk surface can stop the boom.
        float reach = Reach(view, bounds, _ => false, origin, direction);

        TileHit deck = Assert.IsType<TileHit>(view.PickSurface(0, origin, direction, Length));
        TileHit bed = Assert.IsType<TileHit>(TileRaycast.Pick(doc, 0, origin, direction));
        Assert.Equal(0.025f, deck.Point.Y, 1e-4f);
        Assert.Equal(4.725f, reach, 1e-4f);
        Assert.True(reach < bed.Distance - Radius);
    }

    [Fact]
    public void Terrain_is_picked_on_the_observers_nonzero_plane()
    {
        var doc = new TileWorldDocument { PlaneCount = 2 };
        doc.GetOrCreateRegion(TileRenderTestData.Region);
        doc.SetUnderlay(40, 40, 0, TileRenderTestData.Grass);
        doc.SetUnderlay(40, 40, 1, TileRenderTestData.Grass);
        for (int z = 40; z <= 41; z++)
            for (int x = 40; x <= 41; x++)
                doc.SetCornerHeightCm(x, z, 1, 450);
        using TileWorldView view = View(doc, out var bounds);
        view.Observer = new TileCoord(40, 40, 1);

        float reach = Reach(view, bounds, _ => false, new Vector3(40.5f, 6f, -40.5f), -Vector3.UnitY);

        Assert.Equal(1.25f, reach, 1e-4f);
    }

    [Fact]
    public void Terrain_above_a_nonzero_observer_plane_does_not_stop_the_boom()
    {
        var doc = new TileWorldDocument { PlaneCount = 3 };
        doc.GetOrCreateRegion(TileRenderTestData.Region);
        doc.SetUnderlay(40, 40, 1, TileRenderTestData.Grass);
        doc.SetUnderlay(40, 40, 2, TileRenderTestData.Grass);
        using TileWorldView view = View(doc, out var bounds);
        view.Observer = new TileCoord(40, 40, 1);

        float reach = Reach(view, bounds, _ => false, new Vector3(40.5f, 4.5f, -40.5f), Vector3.UnitY);

        Assert.Equal(Length, reach);
    }

    [Fact]
    public void A_top_plane_observer_still_tests_objects_on_its_own_plane()
    {
        var doc = new TileWorldDocument { PlaneCount = 2 };
        doc.GetOrCreateRegion(TileRenderTestData.Region);
        doc.SetUnderlay(40, 40, 1, TileRenderTestData.Grass);
        doc.AddObject("wall", 40, 40, 1, 0);
        using TileWorldView view = View(doc, out var bounds);
        view.Observer = new TileCoord(40, 40, 1);

        float reach = Reach(view, bounds, a => a.Id == "wall",
            new Vector3(41.5f, 4.5f, -40.5f), -Vector3.UnitX);

        Assert.Equal(1.1f, reach, 1e-4f);
    }

    [Fact]
    public void Water_stops_the_boom()
    {
        TileWorldDocument doc = TileRenderTestData.RiverWorld();
        using TileWorldView view = View(doc, out var bounds);
        var origin = new Vector3(31.5f, 2f, -20.5f);
        Vector3 direction = -Vector3.UnitY;

        float reach = Reach(view, bounds, _ => true, origin, direction);

        TileHit water = Assert.IsType<TileHit>(view.PickSurface(0, origin, direction, Length));
        TileHit bed = Assert.IsType<TileHit>(TileRaycast.Pick(doc, 0, origin, direction));
        Assert.Equal(TileRenderTestData.RiverBedCm / 100f, bed.Point.Y, 1e-4f);
        Assert.Equal(water.Distance - Radius, reach, 1e-4f);
        Assert.True(reach < bed.Distance - Radius);
    }

    [Fact]
    public void A_wall_the_filter_accepts_stops_the_boom()
    {
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        using TileWorldView view = View(doc, out var bounds);
        var origin = new Vector3(11.5f, 1.5f, -10.5f);

        float reach = Reach(view, bounds, a => a.Id == "wall", origin, -Vector3.UnitX);

        Assert.Equal(origin.X - (WestWallEastFace(doc) + Radius), reach, 1e-3f);
    }

    [Fact]
    public void A_rejected_nearer_hit_lets_a_farther_accepted_wall_stop_the_boom()
    {
        // A tree inside the house on the west wall's tile, between the pivot and the wall.
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        doc.AddObject("tree", TileRenderTestData.HouseMinX, TileRenderTestData.HouseMinZ, 0, 0);
        using TileWorldView view = View(doc, out var bounds);
        var origin = new Vector3(11.5f, 1.5f, -10.5f);
        var hits = new List<TileObjectHit>();
        Assert.True(view.PickObjects(0, origin, -Vector3.UnitX, Length, bounds, hits) > 0);
        Assert.Equal("tree", doc.FindObject(hits[0].ObjectId)!.ArchetypeId);

        float reach = Reach(view, bounds, a => a.Id == "wall", origin, -Vector3.UnitX);

        Assert.Equal(origin.X - (WestWallEastFace(doc) + Radius), reach, 1e-3f);
    }

    [Fact]
    public void A_wall_the_filter_rejects_does_not_stop_the_boom()
    {
        using TileWorldView view = View(TileRenderTestData.HouseWorld(), out var bounds);

        float reach = Reach(view, bounds, _ => false, new Vector3(11.5f, 1.5f, -10.5f), -Vector3.UnitX);

        Assert.Equal(Length, reach);
    }

    [Fact]
    public void A_visible_roof_on_the_plane_above_stops_the_boom()
    {
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        using TileWorldView view = View(doc, out var bounds);
        view.RoofMode = RoofVisibility.AlwaysVisible;

        float reach = Reach(view, bounds, a => a.IsRoof, new Vector3(11.5f, 1.5f, -10.5f), Vector3.UnitY);

        Assert.Equal(doc.PlaneHeight - 1.5f - Radius, reach, 1e-3f);
    }

    [Fact]
    public void A_hidden_roof_never_stops_the_boom()
    {
        var origin = new Vector3(11.5f, 1.5f, -10.5f);

        using (TileWorldView view = View(TileRenderTestData.HouseWorld(), out var bounds))
        {
            view.RoofMode = RoofVisibility.Interior;
            view.Observer = new TileCoord(11, 10, 0);
            Assert.True(view.ObserverIndoors);

            Assert.Equal(Length, Reach(view, bounds, a => a.IsRoof, origin, Vector3.UnitY));
        }

        using (TileWorldView view = View(TileRenderTestData.HouseWorld(), out var bounds))
        {
            view.RoofMode = RoofVisibility.AlwaysHidden;
            view.Observer = new TileCoord(40, 40, 0);

            Assert.Equal(Length, Reach(view, bounds, a => a.IsRoof, origin, Vector3.UnitY));
        }
    }

    [Fact]
    public void A_box_containing_the_origin_is_skipped()
    {
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        using TileWorldView view = View(doc, out var bounds);
        var origin = new Vector3(WestWallEastFace(doc) + 0.1f, 1.5f, -10.5f);

        float reach = Reach(view, bounds, a => a.Id == "wall", origin, Vector3.UnitY);

        Assert.Equal(Length, reach);
    }
}
