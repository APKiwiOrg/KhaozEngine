using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Render3D;
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

    [Fact]
    public void Terrain_in_an_unloaded_region_is_not_a_visible_surface()
    {
        TileWorldDocument document = TileRenderTestData.RiverWorld();
        var unloaded = new RegionCoord(1, 0);
        document.GetOrCreateRegion(unloaded);
        document.SetUnderlay(unloaded.OriginX, 10, 0, TileRenderTestData.Grass);
        using TileWorldView view = View(document);

        Assert.NotNull(TileRaycast.Pick(
            document,
            0,
            new Vector3(unloaded.OriginX + 0.5f, 5f, -10.5f),
            -Vector3.UnitY));
        Assert.Null(view.PickSurface(
            0,
            new Vector3(unloaded.OriginX + 0.5f, 5f, -10.5f),
            -Vector3.UnitY));
    }

    [Fact]
    public void Picking_continues_past_a_no_draw_tile_to_the_next_rendered_tile()
    {
        var document = new TileWorldDocument { Id = "surface-pick-filter", DisplayName = "Surface pick filter" };
        document.GetOrCreateRegion(Origin);
        document.SetUnderlay(1, 1, 0, TileRenderTestData.Grass);
        document.SetSettings(1, 1, 0, TileSettings.NoDraw);
        document.SetCornerHeightCm(1, 1, 0, 200);
        document.SetCornerHeightCm(2, 1, 0, 200);
        document.SetCornerHeightCm(1, 2, 0, 200);
        document.SetCornerHeightCm(2, 2, 0, 200);
        document.SetUnderlay(3, 1, 0, TileRenderTestData.Grass);
        using TileWorldView view = View(document);
        var origin = new Vector3(0.5f, 3f, -1.5f);
        var direction = new Vector3(1f, -1f, 0f);

        TileHit authored = Assert.IsType<TileHit>(TileRaycast.Pick(document, 0, origin, direction));
        TileHit visible = Assert.IsType<TileHit>(view.PickSurface(0, origin, direction));

        Assert.Equal((1, 1), (authored.X, authored.Z));
        Assert.Equal((3, 1), (visible.X, visible.Z));
        Assert.Equal(0f, visible.Point.Y, 3);
    }

    [Fact]
    public void Picker_cache_is_released_when_its_region_unloads_without_a_draw()
    {
        using TileWorldView view = RiverView(out _);

        Assert.NotNull(view.PickSurface(
            0,
            new Vector3(31.5f, 5f, -10.5f),
            -Vector3.UnitY));
        Assert.Equal(1, view.WaterCacheCount);

        view.UnloadRegion(Origin);

        Assert.Equal(0, view.WaterCacheCount);
    }

    [Fact]
    public void Far_hlod_is_not_pickable_until_its_full_object_enters_gameplay_residency()
    {
        var document = new TileWorldDocument
        {
            Id = "hlod-pick",
            DisplayName = "HLOD pick",
            PlaneCount = 1,
        };
        var far = new RegionCoord(4, 0);
        document.GetOrCreateRegion(far);
        document.SetUnderlay(far.OriginX, far.OriginZ, 0, TileRenderTestData.Grass);
        TileObject tree = document.AddObject("tree", far.OriginX + 32, far.OriginZ + 32, 0, 0);
        var scene = new RecordingTileWorldScene();
        var resolver = new HlodPickResolver();
        var bounds = new TileObjectBoundsCache(resolver);
        var options = new TileWorldViewOptions
        {
            PropLayers = new[]
            {
                new TilePropLayerDefinition
                {
                    Id = "trees",
                    ArchetypeIds = new HashSet<string>(StringComparer.Ordinal) { "tree" },
                    DrawRadius = 576f,
                    LodDistance = 64f,
                    LodCrossfadeWidth = 16f,
                    HlodDistance = 192f,
                    HlodCrossfadeWidth = 32f,
                    HlodWeldCell = 1.5f,
                },
            },
        };
        using var view = new TileWorldView(scene, document, TileRenderTestData.Catalogs, resolver, options,
            new TileWorldBuildQueueOptions(), new ImmediateDispatcher());
        view.LoadRegion(far, TileRegionResidencyState.Decor);
        view.Draw(Vector3.Zero);
        Assert.Equal(1, scene.LiveClusterMeshCount);

        TileObjectArchetype archetype = TileRenderTestData.Catalogs.Archetype("tree")!;
        Vector3 at = TileObjectProps.AnchorPosition(document, archetype, tree);
        var hits = new List<TileObjectHit>();

        Assert.Equal(0, view.PickObjects(0, at + Vector3.UnitY * 10f, -Vector3.UnitY, 20f,
            bounds.TryGetBounds, hits));

        view.LoadRegion(far, TileRegionResidencyState.Gameplay);

        Assert.Equal(1, view.PickObjects(0, at + Vector3.UnitY * 10f, -Vector3.UnitY, 20f,
            bounds.TryGetBounds, hits));
        Assert.Equal(tree.Id, hits[0].ObjectId);
        Assert.Equal("tree", hits[0].ArchetypeId);
    }

    sealed class HlodPickResolver : ITileMeshResolver, ITileLodMeshResolver
    {
        readonly GltfMesh _mesh = MeshPrimitives.Box(4f);
        public IReadOnlyList<GltfMeshPart>? Resolve(TileObjectArchetype archetype) =>
            new[] { new GltfMeshPart(_mesh, default) };
        public IReadOnlyList<GltfMeshPart>? ResolveLod(TileObjectArchetype archetype) =>
            new[] { new GltfMeshPart(_mesh, default) };
        public GltfMesh? ResolveFlatForHlod(TileObjectArchetype archetype) => _mesh;
    }

    sealed class ImmediateDispatcher : IChunkBuildDispatcher
    {
        public void Schedule(Action build) => build();
        public void Drain() { }
    }
}
