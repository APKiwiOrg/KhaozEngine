using System;
using System.Numerics;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>Visible tile surfaces picked through the render view, including the separate planes drawn over
/// authored water beds.</summary>
public class TileWorldViewSurfacePickTests
{
    static readonly RegionCoord Origin = TileRenderTestData.Region;

    static TileWorldView View(TileWorldDocument document)
    {
        var view = new TileWorldView(
            new RecordingTileWorldScene(),
            document,
            TileRenderTestData.Catalogs,
            new GreyboxMeshResolver(document.TileSize, document.PlaneHeight));
        view.LoadRegion(Origin);
        return view;
    }

    static TileWorldView RiverView(out TileWorldDocument document)
    {
        document = TileRenderTestData.RiverWorld();
        return View(document);
    }

    [Fact]
    public void Oblique_ray_picks_the_visible_water_tile_before_the_farther_bed_tile()
    {
        using TileWorldView view = RiverView(out TileWorldDocument document);
        var origin = new Vector3(28.5f, 1f, -10.5f);
        var direction = new Vector3(1f, -0.51f, 0f);

        TileHit bed = Assert.IsType<TileHit>(TileRaycast.Pick(document, 0, origin, direction));
        TileHit surface = Assert.IsType<TileHit>(view.PickSurface(0, origin, direction));

        Assert.Equal((31, 10), (bed.X, bed.Z));
        Assert.Equal((30, 10), (surface.X, surface.Z));
        Assert.Equal(-0.02f, surface.Point.Y, 3);
        Assert.True(surface.Distance < bed.Distance);
    }

    [Fact]
    public void Straight_down_hits_the_water_surface_at_the_original_tile()
    {
        using TileWorldView view = RiverView(out _);

        TileHit hit = Assert.IsType<TileHit>(view.PickSurface(
            0,
            new Vector3(31.5f, 5f, -10.5f),
            -Vector3.UnitY));

        Assert.Equal((31, 10, 0), (hit.X, hit.Z, hit.Plane));
        Assert.Equal(-0.02f, hit.Point.Y, 3);
        Assert.Equal(5.02f, hit.Distance, 3);
    }

    [Fact]
    public void Ordinary_land_falls_back_to_the_authored_terrain_hit()
    {
        using TileWorldView view = RiverView(out _);
        var origin = new Vector3(5.5f, 5f, -5.5f);

        TileHit hit = Assert.IsType<TileHit>(view.PickSurface(0, origin, -Vector3.UnitY));

        Assert.Equal((5, 5, 0), (hit.X, hit.Z, hit.Plane));
        Assert.Equal(new Vector3(5.5f, 0f, -5.5f), hit.Point);
        Assert.Equal(5f, hit.Distance);
    }

    [Fact]
    public void Nearer_authored_terrain_wins_where_it_occludes_the_water_plane()
    {
        using TileWorldView view = RiverView(out _);
        var origin = new Vector3(30.01f, 5f, -10.5f);

        TileHit visible = Assert.IsType<TileHit>(view.PickSurface(0, origin, -Vector3.UnitY));

        Assert.Equal((30, 10), (visible.X, visible.Z));
        Assert.InRange(visible.Point.Y, -0.008f, -0.006f);
        Assert.True(visible.Distance < 5.02f);
    }

    [Fact]
    public void Ray_that_crosses_no_visible_surface_misses()
    {
        using TileWorldView view = RiverView(out _);

        Assert.Null(view.PickSurface(
            0,
            new Vector3(-10.5f, 5f, -10.5f),
            -Vector3.UnitY));
    }

    [Fact]
    public void A_hit_on_the_far_edge_of_a_water_rectangle_stays_on_its_last_tile()
    {
        TileWorldDocument document = TileRenderTestData.RiverWorld();
        document.SetUnderlay(TileRenderTestData.RiverMaxX + 1, 10, 0, 0);
        using TileWorldView view = View(document);

        TileHit hit = Assert.IsType<TileHit>(view.PickSurface(
            0,
            new Vector3(TileRenderTestData.RiverMaxX + 1f, 5f, -10.5f),
            -Vector3.UnitY));

        Assert.Equal((TileRenderTestData.RiverMaxX, 10), (hit.X, hit.Z));
        Assert.Equal(-0.02f, hit.Point.Y, 3);
    }

    [Fact]
    public void Water_surface_respects_max_distance_in_world_metres()
    {
        using TileWorldView view = RiverView(out _);
        var origin = new Vector3(28.5f, 1f, -10.5f);
        var direction = new Vector3(1f, -0.51f, 0f);

        Assert.Null(view.PickSurface(0, origin, direction, 2.2f));
        TileHit hit = Assert.IsType<TileHit>(view.PickSurface(0, origin, direction, 2.3f));
        Assert.InRange(hit.Distance, 2.24f, 2.25f);
    }

    [Fact]
    public void Non_unit_tile_size_transforms_the_cached_water_plane_back_to_its_tile()
    {
        TileWorldDocument document = TileRenderTestData.RiverWorld();
        document.TileSize = 2f;
        using TileWorldView view = View(document);

        TileHit hit = Assert.IsType<TileHit>(view.PickSurface(
            0,
            new Vector3(63f, 5f, -21f),
            -Vector3.UnitY));

        Assert.Equal((31, 10), (hit.X, hit.Z));
        Assert.Equal(63f, hit.Point.X, 3);
        Assert.Equal(-0.02f, hit.Point.Y, 3);
        Assert.Equal(-21f, hit.Point.Z, 3);
    }

    [Fact]
    public void Picking_keeps_the_cached_rendered_plane_until_its_mesh_is_rebuilt()
    {
        using TileWorldView view = RiverView(out TileWorldDocument document);
        var origin = new Vector3(31.5f, 5f, -10.5f);
        Vector3 direction = -Vector3.UnitY;
        view.DrawWaterPlanes();

        TileHit beforeEdit = Assert.IsType<TileHit>(view.PickSurface(0, origin, direction));
        document.SetCornerHeightCm(TileRenderTestData.RiverMinX, 0, 0, 100);
        TileHit beforeRemesh = Assert.IsType<TileHit>(view.PickSurface(0, origin, direction));

        Assert.Equal(-0.02f, beforeEdit.Point.Y, 3);
        Assert.Equal(beforeEdit, beforeRemesh);

        view.MarkDirty(Origin, 0);
        view.Flush();
        TileHit afterRemesh = Assert.IsType<TileHit>(view.PickSurface(0, origin, direction));

        Assert.Equal(0.98f, afterRemesh.Point.Y, 3);
    }

    [Fact]
    public void Picking_after_dispose_throws()
    {
        TileWorldView view = RiverView(out _);
        view.Dispose();

        Assert.Throws<ObjectDisposedException>(() => view.PickSurface(
            0,
            new Vector3(31.5f, 5f, -10.5f),
            -Vector3.UnitY));
    }
}
