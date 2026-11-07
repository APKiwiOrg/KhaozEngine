using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageOwnerRefusalTests
{
    [Theory]
    [InlineData("surface", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("role", MapDocumentRefusal.ReferenceKindMismatch)]
    [InlineData("space", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("space-id", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("owner", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("scope", MapDocumentRefusal.AuthoredOwnershipMismatch)]
    [InlineData("kind", MapDocumentRefusal.AuthoredReferenceKindInvalid)]
    public void EffectiveOwners_KeepTheActualRuleAndLayerContext(string fault, MapDocumentRefusal reason)
    {
        var fixture = FoliageFixtures.Create();
        var layer = fixture.Document.Foliage!.Layers[0];
        fixture.Document.Foliage = null;
        if (fault == "role") fixture.Document.Surfaces.Refs[0] = fixture.Document.Surfaces.Refs[0] with { Role = MapSurfaceRole.Ceiling };
        var resources = MapPrefabResolver.Expand(fixture.Document, fixture.Assets);
        var owner = new MapPrefabReference(MapPrefabReferenceScope.World, MapPrefabChildKind.Surface, FoliageFixtures.FloorId);
        var changed = fault switch
        {
            "surface" => FoliageFixtures.ClearanceLayer(layer, support: FoliageFixtures.Surface("missing")),
            "space" or "space-id" => FoliageFixtures.ClearanceLayer(layer,
                support: new(MapSupportBindingKind.Space, null, fault == "space" ? "missing" : null, 0, 0, 0)),
            "owner" => FoliageFixtures.ClearanceLayer(layer, owners: [new(owner with { Id = "missing" }, layer.Support)]),
            "scope" => FoliageFixtures.ClearanceLayer(layer, owners: [new(owner with { Scope = MapPrefabReferenceScope.Local }, layer.Support)]),
            "kind" => FoliageFixtures.ClearanceLayer(layer, owners: [new(owner with { Kind = (MapPrefabChildKind)99 }, layer.Support)]),
            _ => FoliageFixtures.ClearanceLayer(layer, ordinals: []),
        };
        fixture.Document.Foliage = new(1, [changed]);
        var finding = Assert.Single(MapFoliageReferences.ValidateOwnerFindings(fixture.Document, resources));
        Assert.Equal(reason, finding.Reason);
        Assert.Equal(layer.Id, finding.LayerId);
        Assert.Equal(fault == "role" ? "Support" : fault is "surface" or "space" or "space-id" ? "Support" : "OwnerSupports[0].Owner", finding.FieldPath);
        if (fault is "surface" or "role") Assert.Equal(fault == "surface" ? "missing" : FoliageFixtures.FloorId, finding.SurfaceId);
        if (fault is "owner" or "scope" or "kind")
        {
            Assert.Equal(fault == "owner" ? "missing" : FoliageFixtures.FloorId, finding.EntityId);
            Assert.Equal(MapPrefabReferenceScope.World, finding.ExpectedReferenceScope);
        }
        Assert.Equal(new[] { finding.Message }, MapFoliageReferences.ValidateOwners(fixture.Document, resources));
        if (fault == "role")
        {
            fixture.Document.Foliage = null;
            fixture.Document.Surfaces.Refs[0] = fixture.Document.Surfaces.Refs[0] with { Role = MapSurfaceRole.SupportFloor };
            resources = MapPrefabResolver.Expand(fixture.Document, fixture.Assets);
        }
        fixture.Document.Foliage = new(1, [layer]);
        Assert.Empty(MapFoliageReferences.ValidateOwnerFindings(fixture.Document, resources));
    }
    [Fact]
    public void Expansion_ForwardsTheFirstOwnerFindingAndTheCompleteSnapshot()
    {
        var fixture = FoliageFixtures.Create();
        var layer = fixture.Document.Foliage!.Layers[0];
        var missing = new MapPrefabReference(MapPrefabReferenceScope.World, MapPrefabChildKind.Surface, "missing");
        var other = new MapPrefabReference(MapPrefabReferenceScope.World, MapPrefabChildKind.Marker, "other");
        fixture.Document.Foliage = new(1, [FoliageFixtures.ClearanceLayer(layer,
            owners: [new(missing, layer.Support), new(other, layer.Support)])]);
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabResolver.Expand(fixture.Document, fixture.Assets));
        Assert.Equal(MapDocumentRefusal.ReferenceMissing, error.Reason);
        Assert.Equal(layer.Id, error.LayerId);
        Assert.Equal("missing", error.EntityId);
        Assert.Equal("OwnerSupports[0].Owner", error.FieldPath);
        Assert.Equal(2, error.Findings!.Count);
        Assert.Equal(new[] { "missing", "other" }, error.Findings.Select(f => f.EntityId));
        fixture.Document.Foliage = new(1, [layer]);
        Assert.NotNull(MapPrefabResolver.Expand(fixture.Document, fixture.Assets));
    }

}
