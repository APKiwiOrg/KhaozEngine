using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeProxyProvenanceTests
{
    [Fact]
    public void PublicCluster_MissingOriginalPlacementRefusesLossyTranslation()
    {
        var (scene, routing, original) = Request();
        var rows = original.Instances.Select(i => new NativePropInstance(i.Id, i.NumericId, i.AssetId,
            i.LocalTransform, i.Anchor)).ToArray();
        Assert.Equal(.9f, Assert.Single(rows).LocalTransform.M41);
        var attack = Copy(original, rows);
        Assert.Throws<MapDocumentException>(() => NativeMapPlacements.PrepareCluster(scene, routing, attack));
    }

    [Fact]
    public void PublicCluster_MismatchedOriginalPlacementRefusesBeforeMerge()
    {
        var (scene, routing, original) = Request();
        PropPlacement[] placements = [new("prop-a", -.10000002f, 0, 0, 1, 0, 0)];
        var attack = Copy(original, original.Instances.ToArray(), placements);
        Assert.Throws<MapDocumentException>(() => NativeMapPlacements.PrepareCluster(scene, routing, attack));
    }

    [Fact]
    public void PublicCluster_OriginalPlacementMatchesReleasedCancellation()
    {
        var (scene, routing, original) = Request();
        var sources = original.Layer.HlodSourceMeshes!;
        var expected = PropHlod.BuildMergedMesh([new("prop-a", -.1f, 0, 0, 1, 0, 0)], sources, 1);
        Assert.Equal(0, expected.TriangleCount);
        PropPlacement[] placements = [new("prop-a", -.1f, 0, 0, 1, 0, 0)];
        var frozen = Copy(original, original.Instances.ToArray(), placements);
        placements[0] = new("prop-a", -1, 0, 0, 1, 0, 0);
        Assert.Throws<NotSupportedException>(() => ((IList<PropPlacement>)frozen.WeldPlacements!)[0] = placements[0]);
        using var build = NativeMapPlacements.PrepareCluster(scene, routing, frozen);
        Assert.Equal(expected.Indices32, build.MergedMesh.Indices32);
    }

    [Fact]
    public void PublicCluster_WeldedNormalsUseOriginalBinary32Yaw()
    {
        // The effective yaw rounds to 33554432f, about one radian away. The released route lights
        // welded proxies with that rounded rotation, the same one its positions use.
        var pose = new MapEffectiveTransform(.5, 0, 0, 33554433d, 1);
        Assert.NotEqual(pose.YawRadians, (double)(float)pose.YawRadians);
        var (scene, routing, request) = Request(Wide, pose);
        var expected = PropHlod.BuildMergedMesh([new("prop-a", .5f, 0, 0, 1, 33554432f, 0)], request.Layer.HlodSourceMeshes!, 1);
        Assert.NotEqual(0, expected.TriangleCount);
        Assert.All(expected.Vertices, v => Assert.True(MathF.Abs(v.Normal.Y) < .5f));
        using var build = NativeMapPlacements.PrepareCluster(scene, routing, request);
        Assert.Equal(expected.Indices32, build.MergedMesh.Indices32);
        Assert.Equal(expected.Vertices.Select(v => v.Normal), build.MergedMesh.Vertices.Select(v => v.Normal));
    }

    [Fact]
    public void PublicCluster_ReorderedOriginalPlacementsRefuseBeforeMerge()
    {
        var (scene, routing, original) = Request(2);
        var attack = Copy(original, original.Instances.ToArray(), original.WeldPlacements!.Reverse().ToArray());
        Assert.Throws<MapDocumentException>(() => NativeMapPlacements.PrepareCluster(scene, routing, attack));
    }

    static NativePropClusterRequest Copy(NativePropClusterRequest original, NativePropInstance[] rows,
        IReadOnlyList<PropPlacement>? weldPlacements = null)
    {
        var source = original.Layer;
        var layer = PropLayer.PlacementLayer(rows, source.NativeLods!, source.DrawRadius, source.LodDistances,
            source.LodCrossfadeWidth, source.CastsShadows, source.FadeBandWidth)
            .WithHlod(source.HlodSourceMeshes!, source.HlodDistance, source.HlodWeldCell, source.HlodCrossfadeWidth)
            .WithIdentity(original.Key.LayerId);
        return new(original.Key, original.Generation, original.Area, layer, rows, original.Anchor, weldPlacements);
    }

    static readonly Vector3[] Narrow = [new(.1f, 0, 0), new(.2f, 0, 0), new(.1f, 1, 0),
        new(.1f, 0, 0), new(.2f, 0, 0), new(.1f, 1, 0)];
    // Wide enough that a one-metre weld keeps both triangles at any yaw.
    static readonly Vector3[] Wide = [new(0, 0, 0), new(3, 0, 0), new(0, 3, 0),
        new(0, 0, 0), new(3, 0, 0), new(0, 3, 0)];

    static (MapScopedScene Scene, MapPlacementRouting Routing, NativePropClusterRequest Request) Request(int count = 1) =>
        Request(Narrow, Enumerable.Range(1, count).Select(i => new MapEffectiveTransform(-.1f * i, 0, 0, 0, 1)).ToArray());

    static (MapScopedScene Scene, MapPlacementRouting Routing, NativePropClusterRequest Request) Request(
        Vector3[] positions, params MapEffectiveTransform[] poses)
    {
        var closure = NativeRenderAssetFixtures.Placements(
            coarsestLod: NativeProxyFixtures.Coarsest(positions, sameMaterial: true), sourceUnitsToMetres: 1);
        var scene = NativeMapClusterCellTests.Scene(closure, 1, poses);
        using var assets = NativeMapPlacementTests.Assets(closure);
        var policy = new MapPlacementPolicy([new("selected", new HashSet<string> { "prop-a" }, [10, 20], 4, 1000, 30, 10, 1, true)], 1000);
        var routing = MapPlacementRouting.Create(scene, policy, new HashSet<string>()).Routing!;
        using var batch = NativeMapPlacements.Prepare(scene, assets, policy, routing);
        return (scene, routing, Assert.Single(batch.Clusters).Request);
    }
}
