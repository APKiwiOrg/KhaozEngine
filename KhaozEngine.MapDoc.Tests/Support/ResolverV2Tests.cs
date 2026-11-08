using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.Tests.MapDoc.Foliage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class ResolverV2Tests
{
    [Fact]
    public void MissingBinding_RefusesWithoutMutatingPlacement()
    {
        MapDocument doc = SupportFixtures.CaveWithPlacements();
        var placement = doc.Placements.Single(p => p.Id == "p-surface");
        var binding = placement.SupportBinding;
        placement.SupportBinding = null;
        var error = Assert.Throws<MapDocumentException>(() => MapResolverV2.ResolvePlacements(doc, SupportFixtures.Assets(), [placement]));
        Assert.Equal(MapDocumentRefusal.SupportBindingMissing, error.Reason);
        Assert.Equal(placement.Id, error.PlacementId);
        Assert.Null(placement.Y);
        Assert.Null(placement.SupportBinding);
        placement.SupportBinding = binding;
        Assert.Equal(5f, Assert.Single(MapResolverV2.ResolvePlacements(doc, SupportFixtures.Assets(), [placement])).Transform.Position.Y);
    }

    [Fact]
    public void SupportSpaceLookup_RefusesTheMissingIdentity()
    {
        var error = Assert.Throws<MapDocumentException>(() => MapSupportSelection.RequireSpace([], "missing"));
        Assert.Equal(MapDocumentRefusal.SupportSpaceMissing, error.Reason);
        Assert.Equal("missing", error.SpaceId);
        var space = new MapSpaceDoc("missing", MapSpaceKind.Exterior, null, null, [], [], [], []);
        Assert.Same(space, MapSupportSelection.RequireSpace([space], "missing"));
    }

    [Fact]
    public void ResolverV2_ExplicitYIsUnchangedAndBindingsResolve()
    {
        MapDocument doc = SupportFixtures.CaveWithPlacements();
        MapSupportedResolution r = MapResolverV2.Resolve(doc, SupportFixtures.Assets(), MapDocumentSurfaceSource.Capture(doc), SupportFixtures.V2);
        Assert.Equal(new[] { ("p-explicit", 12.25f), ("p-space", 4.0f), ("p-surface", 5.0f) },
            r.Document.Placements.Select(p => (p.PlacementId, p.Transform.Position.Y)).OrderBy(t => t.PlacementId, StringComparer.Ordinal));
    }
    [Fact]
    public void ResolverV2_AmbiguousBindingRefusesNamingThePlacement()
    {
        MapDocument doc = SupportFixtures.EqualSlabsWithBinding();
        var error = Assert.Throws<MapDocumentException>(() => MapResolverV2.Resolve(doc, SupportFixtures.Assets(), MapDocumentSurfaceSource.Capture(doc), SupportFixtures.V2));
        Assert.Equal(MapDocumentRefusal.SupportUnresolved, error.Reason);
        Assert.Equal("p-amb", error.PlacementId);
        Assert.Equal(MapSupportStatus.Ambiguous, error.SupportStatus);
        var detached = Assert.Throws<MapDocumentException>(() => MapResolverV2.ResolvePlacements(doc, SupportFixtures.Assets(), doc.Placements));
        Assert.Equal(error.Reason, detached.Reason);
        Assert.Equal(error.PlacementId, detached.PlacementId);
        Assert.Equal(error.SupportStatus, detached.SupportStatus);
        var placement = Assert.Single(doc.Placements);
        Assert.Null(placement.Y);
        placement.SupportBinding = new(MapSupportBindingKind.Surface, "slab-a", null, null, null, null);
        Assert.Equal(MapSupportStatus.Supported, Assert.Single(MapResolverV2.Resolve(doc, SupportFixtures.Assets(), MapDocumentSurfaceSource.Capture(doc), SupportFixtures.V2).Supports).Support.Status);
        Assert.Equal(0f, Assert.Single(MapResolverV2.ResolvePlacements(doc, SupportFixtures.Assets(), doc.Placements)).Transform.Position.Y);
    }
    [Fact]
    public void ResolverV2_SpaceBindingWithoutBoundedSearchRefuses()
    {
        MapDocument doc = SupportFixtures.CaveWithPlacementsWithoutBoundedSearch();
        var placement = doc.Placements.Single(p => p.Id == "p-space");
        var error = Assert.Throws<MapDocumentException>(() => MapResolverV2.Resolve(doc, SupportFixtures.Assets(), MapDocumentSurfaceSource.Capture(doc), SupportFixtures.V2));
        Assert.Equal(MapDocumentRefusal.SupportSpaceSearchMissing, error.Reason);
        Assert.Equal("p-space", error.PlacementId);
        Assert.Equal("chamber", error.SpaceId);
        Assert.Null(placement.Y);
        Assert.Null(placement.SupportBinding!.SearchAbove);
        placement.SupportBinding = placement.SupportBinding with { SearchAbove = 0f };
        Assert.Equal(MapSupportStatus.Supported, MapResolverV2.Resolve(doc, SupportFixtures.Assets(), MapDocumentSurfaceSource.Capture(doc), SupportFixtures.V2).Supports.Single(s => s.PlacementId == placement.Id).Support.Status);
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("neither")]
    [InlineData("both")]
    [InlineData("nonfinite")]
    public void ResolverAdoption_RefusesUntilEveryMissingYPlacementIsConverted(string invalid)
    {
        MapDocument v1 = MapDocumentFile.Load(FormatFourFixtures.MonolithicPath);
        var onlyA = new Dictionary<string, MapPlacementSupportChoice> { ["a"] = new(2.5f, null) };
        if (invalid != "missing") onlyA["c"] = invalid switch
        {
            "null" => null!,
            "neither" => new(null, null),
            "both" => new(0, new(MapSupportBindingKind.Surface, "floor", null, null, null, null)),
            _ => new(float.PositiveInfinity, null),
        };
        var error = Assert.Throws<MapDocumentException>(() => MapResolverAdoption.ConvertToAuthoredSupport(v1, onlyA));
        Assert.Equal(invalid == "nonfinite" ? MapDocumentRefusal.ValueNonfinite : MapDocumentRefusal.SupportAdoptionChoiceMissing, error.Reason);
        Assert.Equal("c", error.PlacementId);
        Assert.Null(v1.Placements.Single(p => p.Id == "c").Y);
        if (invalid == "missing")
        {
            MapDocument window = MapDocumentFile.LoadTiled(FormatFourFixtures.CopyTiledToTemp(), new MapTileRect(new(0, 0), new(0, 0)));
            string before = FoliageStorageFixtures.Snapshot(window);
            var partial = Assert.Throws<MapDocumentException>(() => MapResolverAdoption.ConvertToAuthoredSupport(window, onlyA));
            Assert.Equal(MapDocumentRefusal.CompleteDocumentRequired, partial.Reason);
            Assert.Equal(window.Id, partial.DocumentId);
            Assert.Equal(nameof(MapResolverAdoption.ConvertToAuthoredSupport), partial.Operation);
            Assert.Equal(before, FoliageStorageFixtures.Snapshot(window));
        }
        MapDocument adopted = MapResolverAdoption.ConvertToAuthoredSupport(v1, new Dictionary<string, MapPlacementSupportChoice> { ["a"] = new(2.5f, null), ["c"] = new(7.5f, null) });
        Assert.Equal((new MapResolverIdentityDoc(1, 2), MapSupportRecipe.AuthoredBindingsV2), (adopted.ResolverIdentity, adopted.SupportRecipe));
        Assert.Equal(new float?[] { 2.5f, 2.5f, 7.5f }, adopted.Placements.OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => p.Y));
        Assert.Equal(new MapResolverIdentityDoc(1, 1), v1.ResolverIdentity);
    }

    [Theory]
    [InlineData("scale", 1e40, (double)float.MaxValue)]
    [InlineData("scale", 1e-50, 1d)]
    [InlineData("yawRadians", 1e40, (double)float.MaxValue)]
    public void BackendPose_RetainsTheUnrepresentableComponentWithoutChangingTheDocument(string field, double refused, double repaired)
    {
        var document = SupportFixtures.CaveWithPlacements();
        var placement = document.Placements.First();
        placement.Y = 0;
        var assets = SupportFixtures.Assets();
        var source = MapDocumentSurfaceSource.Capture(document);
        var transforms = new Dictionary<string, MapEffectiveTransform>();
        void Pose(double value) => transforms[MapEffectiveContext.Key(MapPrefabChildKind.Placement, placement.Id)] =
            new(0, 0, 0, field == "yawRadians" ? value : 0, field == "scale" ? value : 1);
        Pose(refused);
        string before = FoliageStorageFixtures.Snapshot(document);
        var error = Assert.Throws<MapDocumentException>(() =>
            MapResolverV2.ResolveEffectivePlacements(document, assets, [placement], transforms, source, out _));
        Assert.Equal(MapDocumentRefusal.GeometryNotRepresentable, error.Reason);
        Assert.Equal(placement.Id, error.PlacementId);
        Assert.Equal("$." + field, error.FieldPath);
        Assert.Equal(refused, error.ActualNumber);
        Assert.Equal(nameof(MapResolverV2.ResolveEffectivePlacements), error.Operation);
        Assert.Equal(before, FoliageStorageFixtures.Snapshot(document));
        Pose(repaired);
        var control = Assert.Single(MapResolverV2.ResolveEffectivePlacements(document, assets, [placement], transforms, source, out _));
        Assert.Equal((float)repaired, field == "scale" ? control.Transform.Scale : control.Transform.YawRadians);
    }
}
