using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeMapClusterCellTests
{
    static MapScopedScene Scene(MapAssetClosure closure, float tileSize, params MapEffectiveTransform[] poses)
    {
        using var fixture = NativeSceneFixtures.SeparatedPlacements(closure, tileSize, poses);
        var scope = new MapSceneScope(new(WorldFrame.Origin, new(-32064, -32064), new(32064, 32064), null, null,
            Enum.GetValues<MapSurfaceRole>(), null, new()), new(-32064, -564, -32064, 32064, 564, 32064), MapSceneChannels.Placements);
        var result = NativeSceneFixtures.Open(fixture, false).Acquire(scope);
        Assert.True(result.Witness.CompleteForScope, string.Join("\n", result.Findings));
        return result.Scene!;
    }
    static MapPlacementPolicy Policy(float radius) => new(new[] { new MapPlacementLayer("selected", new HashSet<string> { "prop-a" },
        new float[] { 10, 20 }, 4, radius, 30, 10, 0, true) }, radius);
    static MapDrawSet Record(MapPlacementRenderBatch batch, float focus = 0)
    {
        var recorder = new MapDrawRecorder();
        batch.Register(NativeMapPlacementTests.Stamp, recorder);
        batch.Record(NativeMapPlacementTests.Frame with { Focus = new(WorldFrame.Origin, new(focus, 0, 0)) }, recorder);
        return recorder.Snapshot();
    }
    static string Owner(PropClusterKey key) => FormattableString.Invariant($"cluster/{key.LayerId}/{key.X}/{key.Z}/{key.Plane}");

    [Theory]
    [InlineData(64)]
    [InlineData(128)]
    public void AcceptedSeparatedCells_KeepTheInRangeMemberDrawn(float tileSize)
    {
        var closure = NativeRenderAssetFixtures.Placements();
        var scene = Scene(closure, tileSize, new(0, 0, 0, 0, 1), new(1000, 0, 0, 0, 1));
        using var assets = NativeMapPlacementTests.Assets(closure);
        var policy = Policy(100); var routing = MapPlacementRouting.Create(scene, policy, new HashSet<string>()).Routing!;
        using var batch = NativeMapPlacements.Prepare(scene, assets, policy, routing);
        var before = Record(batch);
        Assert.Equal(2, before.Draws.Count(d => d.Resource.Key.OwnerId == "sparse-0"));
        Assert.DoesNotContain(before.Draws, d => d.Resource.Key.OwnerId == "sparse-1");
        foreach (var build in batch.Clusters) batch.AcceptCluster(build.Request.Key, build, new MeshHandle(90));
        var after = Record(batch);
        var visibleProxy = Assert.Single(after.Draws, d => d.Resource.Key.PartId == "hlod");
        var near = batch.Clusters.Single(c => c.Request.Key.X == 0);
        Assert.Equal(Owner(near.Request.Key), visibleProxy.Resource.Key.OwnerId);
        Assert.Equal(new[] { "sparse-0" }, near.RepresentedIds);
        Assert.Equal(2, batch.Clusters.Count);
        Assert.Equal(new[] { new PropClusterKey("selected", 0, 0, 0), new PropClusterKey("selected", (int)Math.Floor(1000 / tileSize), 0, 0) },
            batch.Clusters.Select(c => c.Request.Key));
        Assert.Equal(before.Draws.Select(d => d.Resource.Key.OwnerId).Distinct(), near.RepresentedIds);
        Assert.Equal(scene.Placements.Select(p => p.PlacementId).Order(), batch.Clusters.SelectMany(c => c.RepresentedIds).Order());
        Assert.All(batch.Clusters, c =>
        {
            Assert.Equal(c.Request.Instances, c.Request.Layer.NativePlacements);
            Assert.Null(c.Request.Layer.Placements);
            Assert.Equal(new float[] { 10, 20 }, c.Request.Layer.LodDistances);
            Assert.Equal(tileSize, c.Request.Area.MaxX - c.Request.Area.MinX);
            Assert.Equal(tileSize, c.Request.Area.MaxZ - c.Request.Area.MinZ);
        });
    }

    [Theory]
    [InlineData(64)]
    [InlineData(96)]
    [InlineData(512)]
    public void FarOriginAcceptedCells_KeepSubMillimetreOffsets(float tileSize)
    {
        var closure = NativeRenderAssetFixtures.Placements(originVertex: true);
        var scene = Scene(closure, tileSize, new(-32000 + .0009, 0, 0, 0, 1), new(32000 + .0009, 0, 0, 0, 1));
        using var assets = NativeMapPlacementTests.Assets(closure);
        var policy = Policy(1000); var routing = MapPlacementRouting.Create(scene, policy, new HashSet<string>()).Routing!;
        using var batch = NativeMapPlacements.Prepare(scene, assets, policy, routing);
        // Coarsest source vertices, authored independently of the decoder and merge under test.
        Vector3[] source = { new(0, 3 * .01f, .04f), new(.08f, 3 * .01f, .04f), new(-.08f, 16 * .01f, .04f),
            new(-.08f, 6 * .01f, .04f), new(.08f, 6 * .01f, .04f), new(-.08f, 18 * .01f, .04f) };
        foreach (var build in batch.Clusters)
        {
            var mesh = build.MergedMesh;
            for (int member = 0; member < build.RepresentedIds.Count; member++)
            {
                double origin = build.RepresentedIds[member] == "sparse-0" ? -32000 : 32000;
                for (int v = 0; v < source.Length; v++)
                {
                    var actual = mesh.Vertices[member * source.Length + v].Position;
                    double x = (double)build.Request.Anchor.X - origin + actual.X;
                    Assert.InRange(Math.Abs(x - (.0009 + source[v].X)), 0, .0001);
                    Assert.InRange(Math.Abs(build.Request.Anchor.Y + (double)actual.Y - source[v].Y), 0, .0001);
                    Assert.InRange(Math.Abs(build.Request.Anchor.Z + (double)actual.Z - source[v].Z), 0, .0001);
                }
            }
            batch.AcceptCluster(build.Request.Key, build, new MeshHandle(90));
        }
        Assert.Equal(2, batch.Clusters.Count);
        foreach (float origin in new[] { -32000f, 32000f })
        {
            var set = Record(batch, origin);
            var draw = Assert.Single(set.Draws, d => d.Resource.Key.PartId == "hlod");
            var mesh = Assert.IsType<MapRigidResource>(set.Resources.Single(r => r.Reference == draw.Resource).Payload).Mesh;
            Assert.InRange(Math.Abs((double)draw.Anchor.X - origin + mesh.Vertices[0].Position.X - .0009), 0, .0001);
        }
    }

    [Fact]
    public void NativeClusterAdapter_RejectsMembersFromAnotherCellBeforeMerge()
    {
        var closure = NativeRenderAssetFixtures.Placements();
        var scene = Scene(closure, 64, new(0, 0, 0, 0, 1), new(1000, 0, 0, 0, 1));
        using var assets = NativeMapPlacementTests.Assets(closure);
        var policy = Policy(100); var routing = MapPlacementRouting.Create(scene, policy, new HashSet<string>()).Routing!;
        using var batch = NativeMapPlacements.Prepare(scene, assets, policy, routing);
        var rows = batch.Clusters.SelectMany(c => c.Request.Instances).ToArray();
        var lods = new Dictionary<string, IReadOnlyList<NativePropLod>>
        {
            ["prop-a"] = new[]
        { new NativePropLod(0, new[] { new MeshHandle(1) }), new NativePropLod(1, new[] { new MeshHandle(2) }), new NativePropLod(2, new[] { new MeshHandle(3) }) }
        };
        var layer = PropLayer.PlacementLayer(rows, lods, 100, new float[] { 10, 20 })
            .WithHlod(batch.Clusters[0].Request.Layer.HlodSourceMeshes!, 30, 0, 10).WithIdentity("selected");
        var attack = new NativePropClusterRequest(new("selected", 0, 0, 0), 0, new(0, 0, 64, 64), layer, rows, default);
        int lowerEntries = 0;
        Assert.Throws<MapDocumentException>(() => NativeMapPlacements.PrepareCluster(scene, routing, attack,
            request => { lowerEntries++; return PropClusterRenderer.BuildNativeCpu(request); }));
        Assert.Equal(0, lowerEntries);
    }

    [Fact]
    public void EffectiveOrigins_SelectHalfOpenCellsBeforeAbsoluteFloatRounding()
    {
        var closure = NativeRenderAssetFixtures.Placements();
        var scene = Scene(closure, 64, new(-32000 - .0009, 0, -.0009, 0, 1), new(32000 - .0009, 0, .0009, 0, 1));
        using var assets = NativeMapPlacementTests.Assets(closure);
        var policy = Policy(1000); var routing = MapPlacementRouting.Create(scene, policy, new HashSet<string>()).Routing!;
        using var batch = NativeMapPlacements.Prepare(scene, assets, policy, routing);
        Assert.Equal(new[] { new PropClusterKey("selected", -501, -1, 0), new PropClusterKey("selected", 499, 0, 0) }, batch.Clusters.Select(c => c.Request.Key));
        Assert.Equal(new[] { new RenderAnchor(-32064, 0, -64), new RenderAnchor(31936, 0, 0) }, batch.Clusters.Select(c => c.Request.Anchor));
    }
}
