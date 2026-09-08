using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>Boundary guarantees of the coarse TileWorld ground mesh.</summary>
public sealed class TileGroundLodTests
{
    static readonly RegionCoord Origin = new(0, 0);

    [Fact]
    public void Coarse4_collapses_a_uniform_region_to_one_pair_per_four_tiles()
    {
        TileWorldDocument doc = FlatWorld(Origin);
        doc.SetCornerHeightCm(0, 0, 0, 25);
        doc.SetCornerHeightCm(63, 0, 0, 50);
        doc.SetCornerHeightCm(0, 63, 0, 75);
        doc.SetCornerHeightCm(63, 63, 0, 100);

        GltfMesh full = Require(doc, Origin, TileGroundLod.Full);
        GltfMesh coarse = Require(doc, Origin, TileGroundLod.Coarse4);

        Assert.Equal(16 * 16 * 2, coarse.TriangleCount);
        Assert.True(coarse.TriangleCount <= full.TriangleCount / 10);
        Assert.Equal(0f, coarse.Vertices.Min(v => v.Position.X));
        Assert.Equal(64f, coarse.Vertices.Max(v => v.Position.X));
        Assert.Equal(-64f, coarse.Vertices.Min(v => v.Position.Z));
        Assert.Equal(0f, coarse.Vertices.Max(v => v.Position.Z));
        AssertCornerHeight(coarse, 0f, 0f, 0.25f);
        AssertCornerHeight(coarse, 64f, 0f, 0.50f);
        AssertCornerHeight(coarse, 0f, -64f, 0.75f);
        AssertCornerHeight(coarse, 64f, -64f, 1f);
    }

    [Fact]
    public void Coarse4_falls_back_for_a_road_strip()
    {
        TileWorldDocument doc = FlatWorld(Origin);
        for (int z = 0; z < 4; z++)
        {
            doc.SetOverlay(1, z, 0, TileRenderTestData.Road);
            doc.SetOverlayShape(1, z, 0, TileOverlayShape.Full);
        }

        Assert.Equal(542, Require(doc, Origin, TileGroundLod.Coarse4).TriangleCount);
    }

    [Fact]
    public void Coarse4_falls_back_at_a_water_edge()
    {
        TileWorldDocument doc = FlatWorld(Origin);
        for (int z = 0; z < 4; z++)
            for (int x = 0; x < 2; x++)
                doc.SetUnderlay(x, z, 0, TileRenderTestData.Water);

        Assert.Equal(542, Require(doc, Origin, TileGroundLod.Coarse4).TriangleCount);
    }

    [Fact]
    public void Coarse4_falls_back_at_a_void_edge()
    {
        TileWorldDocument doc = FlatWorld(Origin);
        doc.SetUnderlay(1, 1, 0, 0);

        Assert.Equal(540, Require(doc, Origin, TileGroundLod.Coarse4).TriangleCount);
    }

    [Fact]
    public void Coarse4_falls_back_for_a_shaped_overlay()
    {
        TileWorldDocument doc = FlatWorld(Origin);
        doc.SetOverlay(1, 1, 0, TileRenderTestData.Road);
        doc.SetOverlayShape(1, 1, 0, TileOverlayShape.DiagonalHalf);

        Assert.Equal(542, Require(doc, Origin, TileGroundLod.Coarse4).TriangleCount);
    }

    [Fact]
    public void Coarse4_falls_back_at_an_underlay_material_boundary()
    {
        TileWorldDocument doc = FlatWorld(Origin);
        for (int z = 0; z < 4; z++)
            for (int x = 0; x < 2; x++)
                doc.SetUnderlay(x, z, 0, 2);

        Assert.Equal(542, Require(doc, Origin, TileGroundLod.Coarse4).TriangleCount);
    }

    [Fact]
    public void Coarse4_falls_back_at_a_bridge_approach_hole()
    {
        TileWorldDocument doc = FlatWorld(Origin);
        doc.SetSettings(1, 1, 0, TileSettings.Bridge | TileSettings.NoDraw);

        Assert.Equal(540, Require(doc, Origin, TileGroundLod.Coarse4).TriangleCount);
    }

    [Fact]
    public void Coarse4_adjacent_regions_share_bit_identical_sloped_edge_positions_and_normals()
    {
        var east = new RegionCoord(1, 0);
        TileWorldDocument doc = FlatWorld(Origin, east);
        for (int z = 0; z < TileRegion.Size; z++)
        {
            doc.SetCornerHeightCm(63, z, 0, 0);
            doc.SetCornerHeightCm(64, z, 0, 100);
            doc.SetCornerHeightCm(65, z, 0, 200);
        }

        GltfMesh westMesh = Require(doc, Origin, TileGroundLod.Coarse4);
        GltfMesh eastMesh = Require(doc, east, TileGroundLod.Coarse4);
        (Vector3 Position, Vector3 Normal)[] west = Edge(westMesh, doc, Origin, TileRegion.Size);
        (Vector3 Position, Vector3 Normal)[] right = Edge(eastMesh, doc, east, 0f);

        Assert.Equal(west, right);
        Assert.All(west, point => Assert.True(point.Normal.X < 0f));
    }

    [Fact]
    public void Decor_residency_queues_and_draws_coarse_ground()
    {
        TileWorldDocument doc = FlatWorld(Origin);
        var scene = new RecordingTileWorldScene();
        var dispatcher = new ManualDispatcher();
        using TileWorldView view = View(scene, doc, dispatcher);

        view.LoadRegion(Origin, TileRegionResidencyState.Decor);
        Assert.Equal(1, dispatcher.PendingCount);
        view.Draw(Vector3.Zero);
        Assert.Empty(scene.Drawn);

        dispatcher.RunAll();
        view.Draw(Vector3.Zero);

        MeshHandle handle = Assert.Single(scene.Drawn).Handle;
        Assert.Equal(512, scene.GroundMeshes[handle.Index].TriangleCount);
    }

    [Fact]
    public void Inward_transition_keeps_coarse_ground_until_full_upload_replaces_it()
    {
        TileWorldDocument doc = FlatWorld(Origin);
        var scene = new RecordingTileWorldScene();
        var dispatcher = new ManualDispatcher();
        using TileWorldView view = View(scene, doc, dispatcher);
        view.LoadRegion(Origin, TileRegionResidencyState.Decor);
        dispatcher.RunAll();
        view.Draw(Vector3.Zero);
        MeshHandle coarse = Assert.Single(scene.Drawn).Handle;

        scene.ClearFrame();
        view.LoadRegion(Origin, TileRegionResidencyState.Gameplay);
        view.Draw(Vector3.Zero);
        Assert.Equal(coarse, Assert.Single(scene.Drawn).Handle);

        scene.ClearFrame();
        dispatcher.RunAll();
        view.Draw(Vector3.Zero);
        MeshHandle full = Assert.Single(scene.Drawn).Handle;
        Assert.NotEqual(coarse, full);
        Assert.Equal(8192, scene.GroundMeshes[full.Index].TriangleCount);
        Assert.Contains(coarse, scene.MeshUnloads);
    }

    [Fact]
    public void Stale_coarse_completion_cannot_replace_requested_full_ground()
    {
        TileWorldDocument doc = FlatWorld(Origin);
        var scene = new RecordingTileWorldScene();
        var dispatcher = new ManualDispatcher();
        using TileWorldView view = View(scene, doc, dispatcher, maxConcurrency: 2);

        view.LoadRegion(Origin, TileRegionResidencyState.Decor);
        view.LoadRegion(Origin, TileRegionResidencyState.Gameplay);
        dispatcher.RunReverse();
        view.Draw(Vector3.Zero);

        MeshHandle handle = Assert.Single(scene.Drawn).Handle;
        Assert.Single(scene.MeshLoads);
        Assert.Equal(8192, scene.GroundMeshes[handle.Index].TriangleCount);
    }

    static TileWorldDocument FlatWorld(params RegionCoord[] regions)
    {
        var doc = new TileWorldDocument
        {
            Id = "coarse-ground",
            DisplayName = "Coarse ground",
            PlaneCount = 1,
        };
        foreach (RegionCoord region in regions)
        {
            doc.GetOrCreateRegion(region);
            for (int z = 0; z < TileRegion.Size; z++)
                for (int x = 0; x < TileRegion.Size; x++)
                    doc.SetUnderlay(region.OriginX + x, region.OriginZ + z, 0, TileRenderTestData.Grass);
        }
        return doc;
    }

    static TileWorldView View(
        RecordingTileWorldScene scene, TileWorldDocument doc, IChunkBuildDispatcher dispatcher,
        int maxConcurrency = 1) =>
        new(scene, doc, TileRenderTestData.Catalogs,
            new GreyboxMeshResolver(doc.TileSize, doc.PlaneHeight), new TileWorldViewOptions(),
            new TileWorldBuildQueueOptions
            {
                MaxConcurrentBuilds = maxConcurrency,
                MaxFullGroundAppliesPerPump = 2,
                MaxCoarseGroundAppliesPerPump = 2,
                MaxHlodAppliesPerPump = 2,
            }, dispatcher);

    static GltfMesh Require(TileWorldDocument doc, RegionCoord region, TileGroundLod lod)
    {
        GltfMesh? mesh = TileGroundMesher.Build(doc, TileRenderTestData.Catalogs, region, 0, lod,
            new TileGroundMesherOptions { JitterAmplitude = 0f });
        Assert.NotNull(mesh);
        return mesh!;
    }

    static void AssertCornerHeight(GltfMesh mesh, float x, float z, float expected) =>
        Assert.Contains(mesh.Vertices, v => v.Position.X == x && v.Position.Z == z && v.Position.Y == expected);

    static (Vector3 Position, Vector3 Normal)[] Edge(
        GltfMesh mesh, TileWorldDocument doc, RegionCoord region, float localX)
    {
        Matrix4x4 world = TileGroundMesher.WorldMatrix(doc, region);
        return mesh.Vertices
            .Where(v => v.Position.X == localX)
            .Select(v => (Vector3.Transform(v.Position, world), v.Normal))
            .Distinct()
            .OrderBy(point => point.Item1.Z)
            .ToArray();
    }

    sealed class ManualDispatcher : IChunkBuildDispatcher
    {
        readonly List<Action> _pending = new();

        public int PendingCount => _pending.Count;

        public void Schedule(Action build) => _pending.Add(build);

        public void RunAll()
        {
            while (_pending.Count > 0)
            {
                Action build = _pending[0];
                _pending.RemoveAt(0);
                build();
            }
        }

        public void RunReverse()
        {
            while (_pending.Count > 0)
            {
                int index = _pending.Count - 1;
                Action build = _pending[index];
                _pending.RemoveAt(index);
                build();
            }
        }

        public void Drain() => RunAll();
    }
}
