using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapSceneDiscoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReverseDiscovery_FindsUnloadedOwnerAndEveryEffectiveReach(bool tiled)
    {
        using var fixture = NativeSceneFixtures.Create("spanning");
        var source = NativeSceneFixtures.Open(fixture, tiled);
        foreach (var window in new[] { MapSceneScopeTests.Window(-64, 0), MapSceneScopeTests.Window() })
        {
            var result = source.Acquire(window);
            Assert.Equal(MapSceneStatus.CompleteForScope, result.Status);
            var scene = Assert.IsType<MapScopedScene>(result.Scene);
            Assert.Contains(scene.Placements, p => p.PlacementId == "spanning-leaf");
            Assert.Contains(MapEffectiveAnnotations.Lights(scene), l => l.Id == "spanning-light");
            Assert.True(result.Witness.CompleteForScope);
            Assert.False(result.Witness.WholeWorldComplete);
            Assert.Equal(-65, scene.Transforms["Placement:spanning-leaf"].X);
        }
    }

    [Fact]
    public void UnloadedNegativeTileOwner_IsDiscoveredAcrossBothSeams()
    {
        using var fixture = NativeSceneFixtures.Create("tile-owner");
        using var document = NativeSceneFixtures.OpenTiled(fixture);
        Assert.Equal(0, document.Tiles.LoadedCount);
        var source = MapSceneSource.Capture(document, fixture.Assets).Source!;
        var row = source.Data.Metadata.Single(r => r.Key == new MapSceneResourceKey("Placement", "spanning-leaf"));
        Assert.Equal(new MapTileCoord(-2, 0), row.StorageOwner);
        foreach (var window in new[] { MapSceneScopeTests.Window(-64, 0), MapSceneScopeTests.Window() })
        {
            var result = source.Acquire(window);
            Assert.True(result.Witness.CompleteForScope, string.Join("\n", result.Findings));
            Assert.Contains(result.Scene!.Placements, p => p.PlacementId == "spanning-leaf");
            Assert.Contains(MapEffectiveAnnotations.Lights(result.Scene), l => l.Id == "spanning-light");
        }
    }

    [Fact]
    public void ExplicitLightFrame_DiscoveryUsesTheReleasedFramePrecedence()
    {
        using var fixture = NativeSceneFixtures.Create("tile-owner");
        fixture.Document.NativeResourceTransforms = new[] { new MapStandaloneResourceTransform(MapPrefabChildKind.Light,
            "spanning-light", new(200, 5, 0, 0, 1), new string('a', 64)) };
        var result = NativeSceneFixtures.Open(fixture, false).Acquire(MapSceneScopeTests.Window(260, 270));
        Assert.True(result.Witness.CompleteForScope);
        var light = Assert.Single(MapEffectiveAnnotations.Lights(result.Scene!));
        Assert.Equal("spanning-light", light.Id);
        Assert.Equal(265, light.Position.X.ToDouble());
        Assert.Contains(result.Scene!.Placements, p => p.PlacementId == "spanning-leaf");
    }

    [Fact]
    public void MovedPrefab_IsDiscoveredAtEffectiveOwnership()
    {
        using var fixture = NativeSceneFixtures.Create("spanning");
        var instance = fixture.Document.Prefabs!.Instances.Single();
        fixture.Document.Prefabs = new(1, 1, new[] { instance with { Transform = new(new(129, 0, 0), 0, 1) } }, []);
        var source = NativeSceneFixtures.Open(fixture, false);
        Assert.Empty(source.Acquire(MapSceneScopeTests.Window()).Scene!.Placements);
        var moved = source.Acquire(MapSceneScopeTests.Window(128, 192));
        Assert.Contains(moved.Scene!.Placements, p => p.PlacementId == "spanning-leaf");
        Assert.Equal(129, moved.Scene.Transforms["Placement:spanning-leaf"].X);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScopedAndFullEffectiveRules_AgreeWithoutPartialDocument(bool tiled)
    {
        using var fixture = NativeSceneFixtures.Create("spanning");
        var full = MapPrefabResolver.Expand(fixture.Document, fixture.Assets);
        long expansions = MapPrefabResolver.ThreadExpansions;
        var scene = NativeSceneFixtures.Open(fixture, tiled).Acquire(MapSceneScopeTests.Window()).Scene!;
        Assert.Equal(expansions, MapPrefabResolver.ThreadExpansions);
        Assert.Equal(full.Placements.Select(p => (p.PlacementId, p.AssetId, p.NumericId, p.Transform)),
            scene.Placements.Select(p => (p.PlacementId, p.AssetId, p.NumericId, p.Transform)));
        Assert.Equal(full.Transforms.OrderBy(p => p.Key), scene.Transforms.OrderBy(p => p.Key));
        Assert.Equal(MapEffectiveAnnotations.Lights(full), MapEffectiveAnnotations.Lights(scene));
    }

    [Fact]
    public void UnrelatedGrowth_DoesNotGrowWindowAcquisition()
    {
        var near = NativeSceneFixtures.Open(NativeSceneFixtures.Create("spanning"), false).Acquire(MapSceneScopeTests.Window());
        var grown = NativeSceneFixtures.Open(NativeSceneFixtures.Create("growth"), false).Acquire(MapSceneScopeTests.Window());
        Assert.Equal(near.Witness.Required, grown.Witness.Required);
        Assert.Equal(near.Work.PayloadReads, grown.Work.PayloadReads);
        Assert.Equal(near.Work.PreparedFaces, grown.Work.PreparedFaces);
        Assert.Equal(near.Work.RetainedBytes, grown.Work.RetainedBytes);
    }

    [Fact]
    public void SeparatedGrowth_VisitsOnlyTheMedianPathAndRejectedSiblings()
    {
        MapSceneResourceMetadata Row(int i) => new(new("Placement", "row-" + i), default,
            new MapBox3(i < 8 ? i : 1024 + i * 4, double.NegativeInfinity, 0,
                i < 8 ? i + 1 : 1025 + i * 4, double.PositiveInfinity, 1), new string('a', 64));
        var small = new MapSceneSpatialIndex(Enumerable.Range(0, 8).Select(Row).ToArray());
        var large = new MapSceneSpatialIndex(Enumerable.Range(0, 136).Select(Row).ToArray());
        var bounds = new MapBox3(-1, -500, -1, 9, 500, 2);
        Assert.Equal(small.Query(bounds, out long smallVisits), large.Query(bounds, out long largeVisits));
        Assert.Equal(1, smallVisits);
        Assert.InRange(largeVisits, 1L, 11L);
    }
}
