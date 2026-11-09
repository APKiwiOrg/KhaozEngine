using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Markers;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapSceneMarkerDiscoveryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SupportMarkerOutsidePhysicalRectangle_DiscoversItsSeat(bool prefab, bool tiled)
    {
        using var empty = NativeSceneFixtures.Create("empty");
        var document = empty.Document;
        var anchor = PrefabFixtures.Patch("anchor", 2, 2, 0);
        anchor.Records.Add(new MapSpaceDoc("outside", MapSpaceKind.Exterior, null, null, [], [], [], []));
        var floor = PrefabFixtures.Patch("seat", 2, 2, 700);
        floor.Key = new("seat", 2, 0);
        floor.Records.Add(new MapSpaceFootprint("seat-domain", new("outside", anchor.Key), floor.Key,
            new[] { 0, 1, 64, 65 }, new(MapBoundKind.SupportFloor, "seat", null), new(MapBoundKind.OpenTop, null, null)));
        foreach (var patch in new[] { anchor, floor })
        {
            document.Surfaces.Refs.Add(PrefabFixtures.Surface(patch.Key.SurfaceId, MapSurfaceRole.SupportFloor));
            document.Surfaces.Patches.Add(patch.Key, patch);
        }
        var marker = MarkerFixtures.Marker("far", 1, -1, new(null, MarkerFixtures.SpaceBinding("outside", 7, 1, 1)));
        var child = new MapPrefabMarkerChild(new(MapPrefabChildKind.Marker, "far"), marker)
        {
            References = [new("/value/supportBinding/spaceId", new(MapPrefabReferenceScope.World, MapPrefabChildKind.Record, "outside"))]
        };
        var asset = new MapPrefabAsset(1, "spanning-markers", new MapPrefabChild[]
        {
            child,
            new MapPrefabMarkerChild(new(MapPrefabChildKind.Marker, "near"),
                MarkerFixtures.Marker("near", -127, -1, new(0, null)))
        });
        var assets = MarkerFixtures.PrefabClosure(asset);
        document.NativeAssets = assets.Roots.ToList();
        if (prefab)
            document.Prefabs = new(1, 1, new[]
            {
                new MapPrefabInstance("span", asset.Id, assets.GetResource(asset.Id).Reference.Sha256, new(new(128, 0, 0), 0, 1), null,
                    new(asset.Children.Select(c => new MapPrefabBinding(c.Key, "span/" + c.Key.Key, null, false)).ToArray()), [])
            }, []);
        else
        {
            document.BuildingIntent = new(1, [], [], [], new[] { marker });
            document.NativeResourceTransforms = new[] { new MapStandaloneResourceTransform(MapPrefabChildKind.Marker, marker.Id, new(128, 0, 0, 0, 1), new string('a', 64)) };
        }
        using var fixture = new NativeSceneFixture(document, assets);
        var full = MapEffectiveAnnotations.Markers(MapPrefabResolver.Expand(document, assets));
        var scope = MapSceneScopeTests.Window(0, 2) with { Channels = MapSceneChannels.Annotations };
        if (!prefab) scope = scope with { DrawBounds = new(128, -10, -2, 130, 10, 0) };
        var result = NativeSceneFixtures.Open(fixture, tiled).Acquire(scope);
        Assert.True(result.Witness.CompleteForScope, string.Join("\n", result.Findings));
        string id = prefab ? "span/far" : "far";
        var actual = MapEffectiveAnnotations.Markers(result.Scene!).Single(m => m.Id == id);
        Assert.Equal(MarkerFixtures.Exact(129, 7, -1), actual.Position);
        var expected = full.Single(m => m.Id == id);
        Assert.Equal(expected.Position, actual.Position);
        Assert.Equal(expected.HeightProvenance, actual.HeightProvenance);
        Assert.Contains(result.Scene!.Surfaces.Patches.Keys, k => k.SurfaceId == "seat");
    }
}
