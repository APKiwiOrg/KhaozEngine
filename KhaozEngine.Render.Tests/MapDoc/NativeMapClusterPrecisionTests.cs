using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeMapClusterPrecisionTests
{
    [Fact]
    public void NativeRegionCell_MatchesReleasedTileRegionSize()
    {
        Assert.Equal(TileRegion.Size, MapPlacementCells.RegionSizeInTiles);
        var closure = NativeRenderAssetFixtures.Placements();
        var scene = NativeMapClusterCellTests.Scene(closure, 1, new MapEffectiveTransform(0, 0, 0, 0, 1), new(63.999, 0, 63.999, 0, 1));
        using var assets = NativeMapPlacementTests.Assets(closure);
        var policy = NativeMapClusterCellTests.Policy(1000);
        var routing = MapPlacementRouting.Create(scene, policy, new HashSet<string>()).Routing!;
        using var batch = NativeMapPlacements.Prepare(scene, assets, policy, routing);
        var cluster = Assert.Single(batch.Clusters);
        Assert.Equal(TileRegion.Size, cluster.Request.Area.MaxX - cluster.Request.Area.MinX);
        Assert.Equal(new[] { "sparse-0", "sparse-1" }, cluster.RepresentedIds);
    }

    [Theory]
    [InlineData(20, 640)]
    [InlineData(40, 2560.0 / 3)]
    public void SubCells_AreEqualAndAlignedToRegionEdges(float tileSize, double edge)
    {
        double region = 64 * (double)tileSize;
        var closure = NativeRenderAssetFixtures.Placements();
        var scene = NativeMapClusterCellTests.Scene(closure, tileSize,
            new MapEffectiveTransform(-region, 0, -region, 0, 1), new(-edge - .0009, 0, -edge - .0009, 0, 1),
            new(-edge, 0, -edge, 0, 1), new(-.0009, 0, -.0009, 0, 1), new(0, 0, 0, 0, 1),
            new(edge - .0009, 0, edge - .0009, 0, 1), new(edge, 0, edge, 0, 1),
            new(region - .0009, 0, region - .0009, 0, 1), new(region, 0, region, 0, 1));
        using var assets = NativeMapPlacementTests.Assets(closure);
        var policy = NativeMapClusterCellTests.Policy(100000);
        var routing = MapPlacementRouting.Create(scene, policy, new HashSet<string>()).Routing!;
        using var batch = NativeMapPlacements.Prepare(scene, assets, policy, routing);
        int n = (int)Math.Ceiling(region / 1024);
        int[] cells = { -n, -2, -1, -1, 0, 0, 1, n - 1, n };
        for (int i = 0; i < cells.Length; i++)
        {
            var request = batch.Clusters.Single(c => c.RepresentedIds.Contains("sparse-" + i)).Request;
            Assert.Equal(new("selected", cells[i], cells[i], 0), request.Key);
            Assert.InRange(Math.Abs(request.Area.MaxX - request.Area.MinX - edge), 0, .0001);
            Assert.InRange(Math.Abs(request.Area.MaxZ - request.Area.MinZ - edge), 0, .0001);
            double start = cells[i] * edge;
            Assert.InRange(Math.Abs(request.Anchor.X + (double)request.Area.MinX - start), 0, .0001);
            Assert.InRange(Math.Abs(request.Anchor.Z + (double)request.Area.MinZ - start), 0, .0001);
        }
    }

    [Fact]
    public void HighAltitudeCell_UsesMemberHeightsForItsAnchor()
    {
        var closure = NativeRenderAssetFixtures.Placements(originVertex: true);
        var scene = NativeMapClusterCellTests.Scene(closure, 1,
            new MapEffectiveTransform(.0009, 32000.0009, 0, 0, 1), new(1.0009, 32001.0009, 0, 0, 1));
        using var assets = NativeMapPlacementTests.Assets(closure);
        var policy = NativeMapClusterCellTests.Policy(1000);
        var routing = MapPlacementRouting.Create(scene, policy, new HashSet<string>()).Routing!;
        using var batch = NativeMapPlacements.Prepare(scene, assets, policy, routing);
        var cluster = Assert.Single(batch.Clusters);
        Assert.Equal(32000, cluster.Request.Anchor.Y);
        var vertices = cluster.MergedMesh.Vertices;
        Assert.InRange(Math.Abs(vertices[0].Position.Y - (.0009 + .03)), 0, .0001);
        Assert.InRange(Math.Abs(vertices[6].Position.Y - (1.0009 + .03)), 0, .0001);
        Assert.All(vertices, v => Assert.InRange(Math.Abs(v.Position.Y), 0, 2048));
    }

    [Theory]
    [InlineData(0, 2048)]
    [InlineData(0, -2048)]
    [InlineData(1, 2048)]
    [InlineData(1, -2048)]
    [InlineData(2, 2048)]
    [InlineData(2, -2048)]
    public void UncertifiedVertex_RefusesWithTypedStatusBeforeBuilder(int axis, double coordinate)
    {
        var closure = NativeRenderAssetFixtures.Placements();
        var scene = NativeMapClusterCellTests.Scene(closure, 1, new MapEffectiveTransform(0, 0, 0, 0, 1));
        using var assets = NativeMapPlacementTests.Assets(closure);
        var policy = NativeMapClusterCellTests.Policy(1000);
        var routing = MapPlacementRouting.Create(scene, policy, new HashSet<string>()).Routing!;
        using var batch = NativeMapPlacements.Prepare(scene, assets, policy, routing);
        var request = Assert.Single(batch.Clusters).Request;
        var meshes = request.Layer.HlodSourceMeshes!.ToDictionary(p => p.Key,
            p => new GltfMesh(p.Value.Vertices.ToArray(), p.Value.Indices32.ToArray()));
        var position = Vector3.Zero;
        if (axis == 0) position.X = (float)coordinate;
        else if (axis == 1) position.Y = (float)coordinate;
        else position.Z = (float)coordinate;
        meshes["prop-a"].Vertices[0].Position = position;
        var layer = request.Layer.WithHlod(meshes, 30, 0, 10);
        var attack = new NativePropClusterRequest(request.Key, request.Generation, request.Area, layer, request.Instances, request.Anchor);
        int lowerEntries = 0;
        var refusal = Assert.Throws<MapRenderAssetRefusal>(() => NativeMapPlacements.PrepareCluster(scene, routing, attack,
            input => { lowerEntries++; return PropClusterRenderer.BuildNativeCpu(input); }));
        Assert.Equal(MapRenderAssetStatus.NotRepresentable, refusal.Status);
        Assert.Equal(0, lowerEntries);
    }

    [Fact]
    public void OversizedMemberGeometry_RefusesTheWholePreparation()
    {
        var closure = NativeRenderAssetFixtures.Placements();
        var scene = NativeMapClusterCellTests.Scene(closure, 1, new MapEffectiveTransform(0, 0, 0, 0, 30000));
        using var assets = NativeMapPlacementTests.Assets(closure);
        var policy = NativeMapClusterCellTests.Policy(100000);
        var routing = MapPlacementRouting.Create(scene, policy, new HashSet<string>()).Routing!;
        var refusal = Assert.Throws<MapRenderAssetRefusal>(() => NativeMapPlacements.Prepare(scene, assets, policy, routing));
        Assert.Equal(MapRenderAssetStatus.NotRepresentable, refusal.Status);
    }

    [Theory]
    [InlineData(.0001, true)]
    [InlineData(.00013, false)]
    public void VertexCertificate_PrecedesBinary32Rounding(double origin, bool certifies)
    {
        var closure = NativeRenderAssetFixtures.Placements();
        var scene = NativeMapClusterCellTests.Scene(closure, 1, new MapEffectiveTransform(origin, 0, 0, 0, 1));
        using var assets = NativeMapPlacementTests.Assets(closure);
        var policy = NativeMapClusterCellTests.Policy(1000);
        var routing = MapPlacementRouting.Create(scene, policy, new HashSet<string>()).Routing!;
        using var batch = NativeMapPlacements.Prepare(scene, assets, policy, routing);
        var request = Assert.Single(batch.Clusters).Request;
        var sources = request.Layer.HlodSourceMeshes!;
        sources["prop-a"].Vertices[0].Position = new(MathF.BitDecrement(2048), 0, 0);
        var layer = request.Layer.WithHlod(sources, 30, 0, 10);
        var probe = new NativePropClusterRequest(request.Key, request.Generation, request.Area, layer, request.Instances, request.Anchor);
        int lowerEntries = 0;
        NativePropClusterCpuBuild Build(NativePropClusterRequest input) { lowerEntries++; return PropClusterRenderer.BuildNativeCpu(input); }
        // Both values round to the same binary32 position. Only the binary64 preflight distinguishes them.
        Assert.Equal(2048, (float)(MathF.BitDecrement(2048) + origin));
        if (certifies)
        {
            var result = NativeMapPlacements.PrepareCluster(scene, routing, probe, Build);
            Assert.Equal(2048, result.MergedMesh.Vertices[0].Position.X);
            Assert.Equal(1, lowerEntries);
        }
        else
        {
            var refusal = Assert.Throws<MapRenderAssetRefusal>(() => NativeMapPlacements.PrepareCluster(scene, routing, probe, Build));
            Assert.Equal(MapRenderAssetStatus.NotRepresentable, refusal.Status);
            Assert.Equal(0, lowerEntries);
        }
    }
}
