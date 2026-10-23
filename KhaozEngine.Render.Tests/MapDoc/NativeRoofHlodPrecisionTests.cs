using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeRoofHlodPrecisionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(65536)]
    public void RoofCohorts_KeepBothEnvelopeSignsAndNonBinarySubdivisions(float tileSize)
    {
        var closure = NativeRenderAssetFixtures.Placements(originVertex: true);
        using var fixture = NativeSceneFixtures.SeparatedPlacements(closure, tileSize,
            new(-32000 + .0009, 0, 0, 0, 1), new(32000 + .0009, 0, 0, 0, 1));
        fixture.Document.BuildingIntent = new(1, [], [], [], []) { Roofs = fixture.Document.Placements
            .Select(p => new MapPrefabReference(MapPrefabReferenceScope.World, MapPrefabChildKind.Placement, p.Id)).ToArray() };
        var source = NativeSceneFixtures.Open(fixture, false);
        var scope = new MapSceneScope(new(WorldFrame.Origin, new(-32064), new(32064), null, null,
            Enum.GetValues<MapSurfaceRole>(), null, new()), new(-32064, -500, -32064, 32064, 500, 32064), MapSceneChannels.Placements | MapSceneChannels.Annotations);
        var scene = source.Acquire(scope).Scene!;
        var policy = new MapPlacementPolicy([new("caps", new HashSet<string> { "prop-a" }, [10, 20], 4, 100000, 30, 10, 0, true)], 100000);
        var routing = MapPlacementRouting.Create(scene, policy, new HashSet<string>()).Routing!;
        using var assets = NativeMapPlacementTests.Assets(closure);
        using var batch = NativeMapPlacements.Prepare(scene, assets, policy, routing);
        Assert.Equal(2, batch.Clusters.Count);
        foreach (var build in batch.Clusters)
        {
            Assert.Equal(1, build.Request.Key.Cohort);
            double origin = build.RepresentedIds[0] == "sparse-0" ? -32000 : 32000;
            double actual = build.Request.Anchor.X + (double)build.MergedMesh.Vertices[0].Position.X;
            Assert.InRange(Math.Abs(actual - (origin + .0009)), 0, .0001);
        }
    }
}
