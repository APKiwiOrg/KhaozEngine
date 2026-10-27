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

    static (MapScopedScene Scene, MapPlacementRouting Routing, NativePropClusterRequest Request) Request(int count = 1)
    {
        Vector3[] positions = [new(.1f, 0, 0), new(.2f, 0, 0), new(.1f, 1, 0),
            new(.1f, 0, 0), new(.2f, 0, 0), new(.1f, 1, 0)];
        var closure = NativeRenderAssetFixtures.Placements(
            coarsestLod: NativeProxyFixtures.Coarsest(positions, sameMaterial: true), sourceUnitsToMetres: 1);
        var scene = NativeMapClusterCellTests.Scene(closure, 1,
            Enumerable.Range(1, count).Select(i => new MapEffectiveTransform(-.1f * i, 0, 0, 0, 1)).ToArray());
        using var assets = NativeMapPlacementTests.Assets(closure);
        var policy = new MapPlacementPolicy([new("selected", new HashSet<string> { "prop-a" }, [10, 20], 4, 1000, 30, 10, 1, true)], 1000);
        var routing = MapPlacementRouting.Create(scene, policy, new HashSet<string>()).Routing!;
        using var batch = NativeMapPlacements.Prepare(scene, assets, policy, routing);
        return (scene, routing, Assert.Single(batch.Clusters).Request);
    }
}
