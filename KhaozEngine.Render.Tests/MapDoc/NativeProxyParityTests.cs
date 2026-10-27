using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeProxyParityTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(.2f)]
    [InlineData(.5f)]
    public void SelectedPlacement_PreservesBinary32WeldPose(float yaw)
    {
        Vector3[] positions = [new(.1f, 0, 0), new(.2f, 0, 0), new(.1f, 1, 0),
            new(.1f, 0, 0), new(.2f, 0, 0), new(.1f, 1, 0)];
        var glb = NativeProxyFixtures.Coarsest(positions, sameMaterial: true);
        var closure = NativeRenderAssetFixtures.Placements(coarsestLod: glb, sourceUnitsToMetres: 1);
        float x = -(.1f * (3f * MathF.Cos(yaw)));
        var scene = NativeMapClusterCellTests.Scene(closure, 1, new MapEffectiveTransform(x, 0, 0, yaw, 3));
        using var assets = NativeMapPlacementTests.Assets(closure);
        var policy = new MapPlacementPolicy([new("selected", new HashSet<string> { "prop-a" }, [10, 20], 4, 1000, 30, 10, 1, true)], 1000);
        var routing = MapPlacementRouting.Create(scene, policy, new HashSet<string>()).Routing!;
        using var batch = NativeMapPlacements.Prepare(scene, assets, policy, routing);
        var build = Assert.Single(batch.Clusters);
        var source = GltfLoader.LoadFlattenedAlbedo(glb, "synthetic-placement");
        var expected = PropHlod.BuildMergedMesh([new("prop-a", x, 0, 0, 3, yaw, 0)],
            new Dictionary<string, GltfMesh> { ["prop-a"] = source }, 1);
        Assert.Equal(0, expected.TriangleCount);
        Assert.Equal(expected.Indices32, build.MergedMesh.Indices32);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(.5f)]
    public void PlacementCancellation_WeldUsesBinary32WorldTransform(float yaw)
    {
        var source = new GltfMesh(
            [new(new(.1f, 0, 0), Vector3.UnitY, Vector4.One),
             new(new(.2f, 0, 0), Vector3.UnitY, Vector4.One),
             new(new(.1f, 1, 0), Vector3.UnitY, Vector4.One)], new uint[] { 0, 1, 2 });
        var sources = new Dictionary<string, GltfMesh> { ["prop"] = source };
        float x = -(.1f * (3f * MathF.Cos(yaw)));
        var placement = new PropPlacement("prop", x, 0, 0, 3, yaw, 0);
        var pose = Matrix4x4.CreateScale(3) * Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(x, 0, 0);
        var instanceAnchor = new RenderAnchor(-1, 0, 0);
        pose.Translation -= new Vector3(-1, 0, 0);
        NativePropInstance[] instances = [new("a", null, "prop", pose, instanceAnchor) { WeldPlacement = placement }];
        var expected = PropHlod.BuildMergedMesh([placement], sources, 1);
        Assert.Equal(0, expected.TriangleCount);
        foreach (int storageX in new[] { -1, 0 })
        {
            var anchor = new RenderAnchor(storageX, 0, 0);
            using var build = PropClusterRenderer.BuildNativeCpu(Request(instances, sources, anchor, 1), true);
            AssertWorldMesh(expected, build.MergedMesh, anchor);
            AssertWorldMesh(PropHlod.BuildPlacementMesh([placement], 0, sources, 1), build.BorrowContribution(0), anchor);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(64)]
    [InlineData(-64)]
    public void OffLatticeAnchor_WeldAndSelectedContributionsMatchWorldMerge(int origin)
    {
        var source = Source();
        var sources = new Dictionary<string, GltfMesh> { ["prop"] = source };
        var placements = new[] { new PropPlacement("prop", origin, origin, origin, scale: 1, yaw: 0, variant: 0),
            new PropPlacement("prop", origin + .125f, origin, origin, scale: 1, yaw: 0, variant: 0) };
        NativePropInstance[] instances = [new("a", null, "prop", Matrix4x4.Identity, new(origin, origin, origin)),
            new("b", null, "prop", Matrix4x4.CreateTranslation(.125f, 0, 0), new(origin, origin, origin))];
        var expected = PropHlod.BuildMergedMesh(placements, sources, 1.5f);
        foreach (int storageOffset in new[] { 0, 1 })
        {
            var anchor = new RenderAnchor(origin + storageOffset, origin - storageOffset, origin + storageOffset);
            using var build = PropClusterRenderer.BuildNativeCpu(Request(instances, sources, anchor, 1.5f), true);
            AssertWorldMesh(expected, build.MergedMesh, anchor);
            for (int member = 0; member < instances.Length; member++)
                AssertWorldMesh(PropHlod.BuildPlacementMesh(placements, member, sources, 1.5f), build.BorrowContribution(member), anchor);
        }
    }

    [Fact]
    public void NonWeldedCluster_PreservesSourceVerticesAttributesAndIndices()
    {
        var source = Source(); var anchor = new RenderAnchor(1, -1, 64);
        NativePropInstance[] instances = [new("a", null, "prop", Matrix4x4.Identity, anchor)];
        using var build = PropClusterRenderer.BuildNativeCpu(Request(instances, new Dictionary<string, GltfMesh> { ["prop"] = source }, anchor, 0));
        Assert.Equal(source.Indices32, build.MergedMesh.Indices32);
        Assert.Equal(source.Vertices, build.MergedMesh.Vertices);
    }

    [Fact]
    public void WeldedCentroids_MatchWorldAccumulationBeforeStorageRebasing()
    {
        var sources = new Dictionary<string, GltfMesh> { ["prop"] = Source() };
        var placements = Enumerable.Range(0, 100).Select(_ => new PropPlacement("prop", 1000, 1000, 1000, 1, 0, 0)).ToArray();
        var instances = Enumerable.Range(0, 100).Select(i => new NativePropInstance("prop-" + i, null, "prop", Matrix4x4.Identity, new(1000, 1000, 1000))).ToArray();
        var expected = PropHlod.BuildMergedMesh(placements, sources, 1.5f);
        var anchor = new RenderAnchor(999, 1001, 1000);
        using var build = PropClusterRenderer.BuildNativeCpu(Request(instances, sources, anchor, 1.5f));
        AssertWorldMesh(expected, build.MergedMesh, anchor);
    }

    [Theory]
    [InlineData(.05f)]
    [InlineData(0)]
    public void MultipartProxy_FlattensWeldedTiersAndPreservesUnweldedParts(float weldCell)
    {
        Vector3[] positions = [new(0, 0, 0), new(8, 0, 0), new(0, 8, 0),
            new(0, 0, 0), new(0, 8, 0), new(0, 0, 8)];
        byte[] glb = NativeProxyFixtures.Coarsest(positions, sameMaterial: true);
        var closure = NativeRenderAssetFixtures.Placements(coarsestLod: glb);
        var scene = NativeMapClusterCellTests.Scene(closure, 1, new MapEffectiveTransform(1, 0, 1, 0, 1));
        using var assets = NativeMapPlacementTests.Assets(closure);
        var policy = new MapPlacementPolicy([new("selected", new HashSet<string> { "prop-a" }, [10, 20], 4, 1000, 30, 10, weldCell, true)], 1000);
        var routing = MapPlacementRouting.Create(scene, policy, new HashSet<string>()).Routing!;
        using var batch = NativeMapPlacements.Prepare(scene, assets, policy, routing);
        var source = Assert.Single(batch.Clusters).Request.Layer.HlodSourceMeshes!["prop-a"];
        var expected = GltfLoader.LoadFlattenedAlbedo(glb, "synthetic-multipart");
        if (weldCell == 0)
        {
            // The unchanged no-decimation route preserves material-part normals and corner multiplicity.
            var parts = GltfLoader.LoadPartsWithMaterials(glb, "synthetic-multipart");
            var vertices = parts.SelectMany(p => p.Mesh.Vertices.Select(v =>
            {
                v.Color *= new Vector4(GltfLoader.AverageAlbedo(p.Maps.Albedo!.Value), 1); return v;
            })).ToArray();
            expected = new(vertices, new uint[] { 0, 1, 2, 3, 4, 5 });
        }
        Assert.Equal(weldCell > 0 ? 4 : 6, expected.Vertices.Length);
        Assert.Equal(expected.Indices32, source.Indices32);
        Assert.Equal(expected.Vertices.Length, source.Vertices.Length);
        for (int v = 0; v < expected.Vertices.Length; v++)
        {
            var vertex = expected.Vertices[v]; vertex.Position *= .01f;
            Assert.Equal(vertex, source.Vertices[v]);
        }
        // Individual tier parts and their material boundaries remain untouched by proxy flattening.
        Assert.Equal(2, assets.ById["prop-a"].Lods[^1].Parts.Count);
        Assert.All(assets.ById["prop-a"].Lods[^1].Parts, p => Assert.Equal(3, p.Mesh.Vertices.Length));
    }

    static NativePropClusterRequest Request(NativePropInstance[] instances, IReadOnlyDictionary<string, GltfMesh> sources,
        RenderAnchor anchor, float cell)
    {
        var lods = new Dictionary<string, IReadOnlyList<NativePropLod>> { ["prop"] = new[] { new NativePropLod(0, new[] { new MeshHandle(1) }) } };
        var layer = PropLayer.PlacementLayer(instances, lods, 1000, Array.Empty<float>()).WithHlod(sources, 30, cell, 10);
        return new(new("proxy", 0, 0, 0), 0, new(0, 0, 64, 64), layer, instances, anchor);
    }

    static GltfMesh Source() => new(
        [new(new(.1f, .1f, .1f), Vector3.UnitY, new(1, 0, 0, 1)),
         new(new(1.1f, .1f, .1f), Vector3.UnitY, new(0, 1, 0, 1)),
         new(new(.1f, 2.1f, .1f), Vector3.UnitY, new(0, 0, 1, 1)),
         new(new(.1f, .1f, 2.1f), Vector3.UnitY, Vector4.One)], new uint[] { 0, 1, 2, 0, 2, 3 });

    static void AssertWorldMesh(GltfMesh expected, GltfMesh actual, RenderAnchor anchor)
    {
        Assert.Equal(expected.Indices32, actual.Indices32);
        Assert.Equal(expected.Vertices.Length, actual.Vertices.Length);
        for (int v = 0; v < expected.Vertices.Length; v++)
        {
            var want = expected.Vertices[v]; var got = actual.Vertices[v];
            Assert.InRange(Math.Abs(want.Position.X - (anchor.X + (double)got.Position.X)), 0, .0001);
            Assert.InRange(Math.Abs(want.Position.Y - (anchor.Y + (double)got.Position.Y)), 0, .0001);
            Assert.InRange(Math.Abs(want.Position.Z - (anchor.Z + (double)got.Position.Z)), 0, .0001);
            Assert.Equal(want.Normal, got.Normal); Assert.Equal(want.Color, got.Color);
        }
    }
}
