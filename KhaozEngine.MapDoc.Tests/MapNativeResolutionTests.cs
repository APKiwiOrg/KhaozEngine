using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapNativeResolutionTests
{
    [Fact]
    public void ResolverOne_MatchesTheAnalyticResolver()
    {
        var f = NativeResolverFixtures.Create();
        f.Document.Placements[0].Y = null;
        MapResolvedDocument expected = MapResolver.Resolve(f.Document, f.Assets, (_, _) => 7f, f.Options);
        MapResolvedDocument actual = MapNativeResolution.Resolve(f.Document, f.Assets, f.Options, (_, _) => 7f);
        Assert.Equal(expected.AuthoredHash, actual.AuthoredHash);
        Assert.Equal(expected.Placements.Select(p => (p.PlacementId, p.Transform)),
            actual.Placements.Select(p => (p.PlacementId, p.Transform)));
    }

    [Fact]
    public void ResolverTwo_MatchesTheAuthoredBindingResolver()
    {
        MapDocument document = SupportFixtures.CaveWithPlacements();
        MapResolvedDocument expected = MapResolverV2.Resolve(document, SupportFixtures.Assets(),
            MapDocumentSurfaceSource.Capture(document), SupportFixtures.V2).Document;
        MapResolvedDocument actual = MapNativeResolution.Resolve(document, SupportFixtures.Assets(), SupportFixtures.V2);
        Assert.Equal(expected.AuthoredHash, actual.AuthoredHash);
        Assert.Equal(expected.Placements.Select(p => (p.PlacementId, p.Transform)),
            actual.Placements.Select(p => (p.PlacementId, p.Transform)));
    }

    [Fact]
    public void ResolverTwoWithResolverOneOptions_Refuses()
    {
        MapDocument document = SupportFixtures.CaveWithPlacements();
        var options = SupportFixtures.V2 with { ResolverVersion = 1 };
        var error = Assert.Throws<MapDocumentException>(() =>
            MapNativeResolution.Resolve(document, SupportFixtures.Assets(), options, (_, _) => 0f));
        Assert.Equal(MapDocumentRefusal.ResolverContractMismatch, error.Reason);
        Assert.Equal(1, error.PayloadVersion);
        Assert.Equal(2, error.ResolverVersion);
        Assert.Equal(1, error.OptionsResolverVersion);
        Assert.Equal(2, error.ExpectedResolverVersion);
        Assert.Equal("options.resolverVersion", error.FieldPath);
        Assert.NotNull(MapNativeResolution.Resolve(document, SupportFixtures.Assets(), SupportFixtures.V2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolverOne_RequiresLegacyHeightOnWholeAndSubsetPaths(bool subset)
    {
        var f = NativeResolverFixtures.Create();
        f.Document.Placements[0].Y = null;
        string before = MapDocumentFile.SaveText(f.Document);
        var error = Assert.Throws<MapDocumentException>(() =>
        {
            if (subset) MapNativeResolution.ResolvePlacements(f.Document, f.Assets, f.Options, ["a"]);
            else MapNativeResolution.Resolve(f.Document, f.Assets, f.Options);
        });
        Assert.Equal(MapDocumentRefusal.SupportLegacyHeightRequired, error.Reason);
        Assert.Equal(1, error.ResolverVersion);
        Assert.Equal("legacySupportHeight", error.FieldPath);
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));

        var control = subset
            ? MapNativeResolution.ResolvePlacements(f.Document, f.Assets, f.Options, ["a"], (_, _) => 0f)
            : MapNativeResolution.Resolve(f.Document, f.Assets, f.Options, (_, _) => 0f).Placements;
        Assert.Equal(0f, control.Single(p => p.PlacementId == "a").Transform.Position.Y);
    }

    [Theory]
    [InlineData(false, "a")]
    [InlineData(true, "a/item")]
    public void ResolvePlacements_RequiresRequestedPlacement(bool prefab, string presentId)
    {
        var legacy = NativeResolverFixtures.Create();
        var effective = PrefabFixtures.Create("roomless");
        var document = prefab ? effective.Document : legacy.Document;
        var assets = prefab ? effective.Assets : legacy.Assets;
        var options = prefab ? PrefabFixtures.Options() : legacy.Options;
        string before = MapDocumentFile.SaveText(document);
        var error = Assert.Throws<MapDocumentException>(() =>
            MapNativeResolution.ResolvePlacements(document, assets, options, ["missing-placement"], (_, _) => 0f));
        Assert.Equal(MapDocumentRefusal.ReferenceMissing, error.Reason);
        Assert.Equal("missing-placement", error.PlacementId);
        Assert.Equal("missing-placement", error.EntityId);
        Assert.Equal("placement", error.SubjectKind);
        Assert.Equal(before, MapDocumentFile.SaveText(document));

        var control = MapNativeResolution.ResolvePlacements(document, assets, options, [presentId], (_, _) => 0f);
        Assert.Equal(presentId, Assert.Single(control).PlacementId);
    }
}
