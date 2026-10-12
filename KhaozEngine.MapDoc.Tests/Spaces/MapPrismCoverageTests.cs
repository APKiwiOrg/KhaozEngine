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

public sealed class MapPrismCoverageTests
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

    [Theory]
    [InlineData(0.8f, false)]
    [InlineData(1.2f, true)]
    public void TransformedCanonicalPartition_FinalNarrowingRefusesConservatively(float scale, bool far)
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Sloped(far, scale));
        var view = r.CompleteView();
        var prism = view.RecordsIn(new("a/floor", 0, 0)).OfType<MapPrismDomain>().Single();
        var ring = MapPrismGeometry.Compile(view, prism).Pieces.Single().Ring.Select(MapWideXz.From).ToArray();
        int zeroAreaRefusals = 0, positiveAreaRefusals = 0;
        foreach (var key in view.Witness.Present.Select(patch => patch.Key).Where(key => key.SurfaceId == "exterior"))
        {
            var compiled = MapEffectiveGeometry.Patch(view, key);
            foreach (var face in compiled.Faces)
            {
                var polygon = MapRefinementPolygon.Triangle(compiled.ExactTriangle(face)).Select(MapWideXz.From).ToArray();
                var intersection = MapEffectiveGeometry.Intersect(polygon, ring);
                if (!intersection.Any(point => !point.TryNarrow(out _))) continue;
                if (MapEffectiveGeometry.AreaWide(intersection).Sign == 0) zeroAreaRefusals++;
                else positiveAreaRefusals++;
            }
        }
        Assert.True(positiveAreaRefusals > 0);
        Assert.Equal(0, zeroAreaRefusals);
        var p = PrefabFixtures.SharedFloorCorner(r);
        Assert.Throws<MapExactOverflowException>(() => MapOccupiedPartitions.Build(view, new(p.X.Subtract(new(10, 1)), p.Z.Subtract(new(10, 1))),
            new(p.X.Add(new(10, 1)), p.Z.Add(new(10, 1))), new(), false));
        Assert.Contains(MapPrismCoverage.Validate(view, new()), e => e.Contains("not representable", StringComparison.Ordinal));
    }
}
