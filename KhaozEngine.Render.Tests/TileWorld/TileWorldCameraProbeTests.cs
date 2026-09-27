using System;
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

    // The bridge of TileWorldViewSurfacePickTests: a 3x3 deck 0.825 m above an anchor on a bed carved 80 cm down.
    // The filter rejects every archetype, because the bridge's greybox model box stands above its deck and would
    // otherwise stop the boom first. What is under test is the walk surface alone.
    const int BridgeZ = 40;

    static TileWorldCatalogs BridgeCatalogs() => TileWorldCatalogs.Merge(
        TileRenderTestData.Catalogs,
        TileWorldCatalogs.LoadJson(
            """
            {
              "archetypes": [
                { "id": "bridge", "name": "Bridge", "meshRef": "test/bridge.glb", "sizeX": 3, "sizeZ": 3,
                  "walkSurfaces": [ { "height": 0.825, "minX": -2.5, "maxX": 2.5, "minZ": -1.5, "maxZ": 1.5 } ] }
              ]
            }
            """,
            "camera-probe-walk-surfaces"));

    [Fact]
    public void A_walk_surface_stops_the_boom()
    {
        TileWorldDocument doc = TileRenderTestData.RiverWorld();
        for (int z = BridgeZ; z <= BridgeZ + 3; z++)
            for (int x = TileRenderTestData.RiverMinX + 1; x <= TileRenderTestData.RiverMaxX; x++)
                doc.SetCornerHeightCm(x, z, 0, -80);
        doc.AddObject("bridge", TileRenderTestData.RiverMinX, BridgeZ, 0, 0);
        using TileWorldView view = View(doc, BridgeCatalogs(), out var bounds);
        var origin = new Vector3(31.5f, 5f, -41.5f);
        Vector3 direction = -Vector3.UnitY;

        float reach = Reach(view, bounds, _ => false, origin, direction);

        TileHit deck = Assert.IsType<TileHit>(view.PickSurface(0, origin, direction, Length));
        TileHit bed = Assert.IsType<TileHit>(TileRaycast.Pick(doc, 0, origin, direction));
        Assert.Equal(deck.Distance - Radius, reach, 1e-4f);
        Assert.True(reach < bed.Distance - Radius);
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
