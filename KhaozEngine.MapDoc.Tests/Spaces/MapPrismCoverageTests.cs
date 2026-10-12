using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Spaces;

public sealed partial class MapPrismCoverageTests
{
    [Theory]
    [InlineData("basic")]
    [InlineData("adjacent")]
    [InlineData("reflex-storeys")]
    [InlineData("uneven-parent")]
    public void ValidPrisms_PublishWithoutFabricatedWalls(string variant)
    {
        var r = PrefabFixtures.Effective(variant == "basic" ? PrefabFixtures.Create() : PrefabFixtures.Cave(variant));
        Assert.Empty(MapSpaceCoverageValidator.Validate(r.CompleteView()));
    }

    [Fact]
    public void PrismWithNoAuthoredParent_Refuses() => Assert.Contains(
        MapSpaceCoverageValidator.Validate(PrefabFixtures.Effective(PrefabFixtures.Cave("missing-parent")).CompleteView()),
        e => e.Contains("parent", StringComparison.Ordinal));

    [Fact]
    public void OutsideParentFootprint_Refuses() => Assert.Contains(
        MapSpaceCoverageValidator.Validate(PrefabFixtures.Effective(PrefabFixtures.Cave("outside-parent")).CompleteView()),
        e => e.Contains("parent footprint", StringComparison.Ordinal));

    [Theory]
    [InlineData("zero-height")]
    [InlineData("self-intersection")]
    public void ZeroHeightOrSelfIntersectingPrism_Refuses(string variant) => Assert.Contains(
        MapSpaceCoverageValidator.Validate(PrefabFixtures.Effective(PrefabFixtures.Cave(variant)).CompleteView()),
        e => e.Contains("prism", StringComparison.Ordinal));

    [Fact]
    public void StandaloneStorey_RoundTripsNameAndRoomOwnership()
    {
        var f = PrefabFixtures.StandalonePrism();
        var copy = PrefabFixtures.ReloadPrisms(f);
        Assert.Equal("a/lower", copy.Document.BuildingIntent!.Storeys.Single().Id);
        Assert.Equal("Lower", copy.Document.BuildingIntent.Storeys.Single().Name);
        Assert.Equal("a/room", new MapSpaceMembership(PrefabFixtures.Effective(copy).CompleteView())
            .QueryExact(new(new(2, 1), new(1, 1), new(-2, 1))).SpaceId);
        copy.Document.BuildingIntent = new(1, [], [], [], [])
        { Storeys = new[] { copy.Document.BuildingIntent.Storeys.Single() with { InstanceId = "a" } } };
        Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveText(copy.Document));
    }

    [Fact]
    public void UndeclaredLocalStorey_RefusesBeforeExpansion()
    {
        var f = PrefabFixtures.Create();
        var asset = f.Assets.GetPrefab("hut");
        Assert.Throws<MapDocumentException>(() => MapPrefabFile.Write(asset with
        { Children = asset.Children.Where(c => c.Key.Kind != MapPrefabChildKind.Storey).ToArray() }));
    }

}
