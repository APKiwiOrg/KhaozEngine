using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Interiors;
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

    [Theory]
    [InlineData("missing-parent", "parent")]
    [InlineData("outside-parent", "parent footprint")]
    [InlineData("zero-height", "prism")]
    [InlineData("self-intersection", "prism")]
    public void InvalidPrisms_Refuse(string variant, string diagnostic) => Assert.Contains(
        MapSpaceCoverageValidator.Validate(PrefabFixtures.Effective(PrefabFixtures.Cave(variant)).CompleteView()),
        e => e.Contains(diagnostic, StringComparison.Ordinal));

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

    // #1398. MapDocumentValidator does not validate topology references before prefab validation runs, so the storey
    // refusal built for a prism built in code without a space must not dereference the space. This needs two defects
    // by nature, because the dereference only ran while a storey refusal was being built.
    [Fact]
    public void StandaloneStorey_RefusesAPrismWithoutASpaceThroughTheDocumentValidator()
    {
        var f = PrefabFixtures.StandalonePrism();
        string? prismId = null;
        foreach (var patch in f.Document.Surfaces.Patches.Values)
            for (int i = 0; i < patch.Records.Count; i++)
                if (patch.Records[i] is MapPrismDomain prism && prismId is null)
                {
                    prismId = prism.Id;
                    patch.Records[i] = prism with { Space = null! };
                }
        Assert.NotNull(prismId);
        f.Document.BuildingIntent = new(1, [], [], [], []) { Storeys = Array.Empty<MapStoreyDoc>() };

        var findings = MapDocumentValidator.ValidateFindings(f.Document, MapDocRegistry.CreateDefault());

        var storey = Assert.Single(findings, x => x.SubjectKind == "storey");
        Assert.Equal(MapDocumentRefusal.ReferenceMissing, storey.Reason);
        Assert.Equal(prismId, storey.RecordId);
        Assert.Null(storey.SpaceId);
    }
}
