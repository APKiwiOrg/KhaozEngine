using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageLocalRefusalTests
{
    [Theory]
    [InlineData("missing", MapDocumentRefusal.SchemaValueRequired, "Support")]
    [InlineData("surface-id", MapDocumentRefusal.SchemaValueRequired, "Support.surfaceId")]
    [InlineData("surface-extra", MapDocumentRefusal.AuthoredOwnershipMismatch, "Support.spaceId")]
    [InlineData("space-id", MapDocumentRefusal.SchemaValueRequired, "Support.spaceId")]
    [InlineData("space-extra", MapDocumentRefusal.AuthoredOwnershipMismatch, "Support.surfaceId")]
    [InlineData("reference-missing", MapDocumentRefusal.SchemaValueRequired, "Support.referenceY")]
    [InlineData("reference-nonfinite", MapDocumentRefusal.ValueNonfinite, "Support.referenceY")]
    [InlineData("below-missing", MapDocumentRefusal.SchemaValueRequired, "Support.searchBelow")]
    [InlineData("below-nonfinite", MapDocumentRefusal.ValueNonfinite, "Support.searchBelow")]
    [InlineData("below-negative", MapDocumentRefusal.ValueOutsideRange, "Support.searchBelow")]
    [InlineData("above-missing", MapDocumentRefusal.SchemaValueRequired, "Support.searchAbove")]
    [InlineData("above-nonfinite", MapDocumentRefusal.ValueNonfinite, "Support.searchAbove")]
    [InlineData("above-negative", MapDocumentRefusal.ValueOutsideRange, "Support.searchAbove")]
    [InlineData("kind", MapDocumentRefusal.SchemaEnumUnsupported, "Support.kind")]
    public void BindingRules_KeepTheirLocalSubjectAndRepairedBoundary(string fault, MapDocumentRefusal reason, string field)
    {
        var layer = FoliageFixtures.BasicLayer([]);
        var surface = layer.Support;
        var space = new MapSupportBinding(MapSupportBindingKind.Space, null, "hall", 0, 0, 0);
        MapSupportBinding? broken = fault switch
        {
            "missing" => null,
            "surface-id" => surface with { SurfaceId = " " },
            "surface-extra" => surface with { SpaceId = "hall" },
            "space-id" => space with { SpaceId = " " },
            "space-extra" => space with { SurfaceId = "floor" },
            "reference-missing" => space with { ReferenceY = null },
            "reference-nonfinite" => space with { ReferenceY = float.NaN },
            "below-missing" => space with { SearchBelow = null },
            "below-nonfinite" => space with { SearchBelow = float.PositiveInfinity },
            "below-negative" => space with { SearchBelow = -1 },
            "above-missing" => space with { SearchAbove = null },
            "above-nonfinite" => space with { SearchAbove = float.NegativeInfinity },
            "above-negative" => space with { SearchAbove = -1 },
            _ => surface with { Kind = (MapSupportBindingKind)99 },
        };
        // A layer requires a nonnull binding at construction. A nested owner support admits the missing value.
        var owner = FoliageFixtures.Owner("owner");
        var draft = new FoliageLayerDraft(layer);
        if (broken is null) draft.OwnerSupports = [new(owner, null!)];
        else draft.Support = broken;
        var findings = MapFoliageValidation.ValidateFindings(new(1, [draft.Build()]));
        var finding = Assert.Single(findings);
        Assert.Equal(reason, finding.Reason);
        Assert.Equal(layer.Id, finding.LayerId);
        Assert.Equal(broken is null ? "OwnerSupports[0].Support" : field, finding.FieldPath);
        Assert.Equal(broken?.SurfaceId, finding.SurfaceId);
        Assert.Equal(broken?.SpaceId, finding.SpaceId);
        if (reason == MapDocumentRefusal.SchemaEnumUnsupported)
        {
            Assert.Equal(typeof(MapSupportBindingKind), finding.EnumType);
            Assert.Equal(99L, finding.ActualInteger);
        }
        if (reason == MapDocumentRefusal.ValueOutsideRange)
        {
            Assert.Equal(-1d, finding.ActualNumber);
            Assert.Equal(0d, finding.MinimumNumber);
        }
        draft.Support = fault.StartsWith("surface", StringComparison.Ordinal) || fault == "kind" ? surface : space;
        draft.OwnerSupports = broken is null ? [new(owner, surface)] : [];
        Assert.Empty(MapFoliageValidation.ValidateFindings(new(1, [draft.Build()])));
    }

    [Theory]
    [InlineData("missing", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("scope", MapDocumentRefusal.AuthoredOwnershipMismatch)]
    [InlineData("kind", MapDocumentRefusal.AuthoredReferenceKindInvalid)]
    [InlineData("id", MapDocumentRefusal.SchemaValueRequired)]
    public void OwnerRules_SelectTheFailedBranchAndRetainTheReference(string fault, MapDocumentRefusal reason)
    {
        var layer = FoliageFixtures.BasicLayer([]);
        var owner = FoliageFixtures.Owner("owner");
        var broken = fault switch
        {
            "missing" => null,
            "scope" => owner with { Scope = MapPrefabReferenceScope.Local },
            "kind" => owner with { Kind = (MapPrefabChildKind)99 },
            _ => owner with { Id = "" },
        };
        var draft = new FoliageLayerDraft(layer) { OwnerSupports = [new(broken!, layer.Support)] };
        var finding = Assert.Single(MapFoliageValidation.ValidateFindings(new(1, [draft.Build()])));
        Assert.Equal(reason, finding.Reason);
        Assert.Equal(layer.Id, finding.LayerId);
        Assert.Equal("OwnerSupports[0].Owner", finding.FieldPath);
        Assert.Equal(0, finding.Index);
        Assert.Equal(broken?.Id, finding.EntityId);
        Assert.Equal(broken?.Kind, finding.ChildKind);
        Assert.Equal(broken?.Scope, finding.ReferenceScope);
        Assert.Equal(MapPrefabReferenceScope.World, finding.ExpectedReferenceScope);
        draft.OwnerSupports = [new(owner, layer.Support)];
        Assert.Empty(MapFoliageValidation.ValidateFindings(new(1, [draft.Build()])));
    }

    [Theory]
    [InlineData("rule", "kind", MapDocumentRefusal.SchemaEnumUnsupported, "Kind")]
    [InlineData("mask", "kind", MapDocumentRefusal.SchemaEnumUnsupported, "Kind")]
    [InlineData("rule", "min-x", MapDocumentRefusal.ValueNonfinite, "Footprint.MinX")]
    [InlineData("rule", "min-z", MapDocumentRefusal.ValueNonfinite, "Footprint.MinZ")]
    [InlineData("rule", "max-x", MapDocumentRefusal.ValueNonfinite, "Footprint.MaxX")]
    [InlineData("rule", "max-z", MapDocumentRefusal.ValueNonfinite, "Footprint.MaxZ")]
    [InlineData("rule", "width", MapDocumentRefusal.BoundsInvalid, "Footprint.MaxX")]
    [InlineData("mask", "depth", MapDocumentRefusal.BoundsInvalid, "Footprint.MaxZ")]
    public void ClearanceRules_RetainItemContextAndTheFirstFailedCoordinate(string source, string fault, MapDocumentRefusal reason, string field)
    {
        var layer = FoliageFixtures.BasicLayer([]);
        var rect = fault switch
        {
            "min-x" => new MapFoliageRect(float.NaN, 0, 1, 1),
            "min-z" => new MapFoliageRect(0, float.NaN, 1, 1),
            "max-x" => new MapFoliageRect(0, 0, float.PositiveInfinity, 1),
            "max-z" => new MapFoliageRect(0, 0, 1, float.PositiveInfinity),
            "width" => new MapFoliageRect(0, 0, 0, 1),
            "depth" => new MapFoliageRect(0, 0, 1, 0),
            _ => new MapFoliageRect(0, 0, 1, 1),
        };
        var kind = fault == "kind" ? (MapFoliageClearanceKind)99 : MapFoliageClearanceKind.Door;
        var rule = new MapFoliageClearanceRule("item", "tree", null, kind, rect);
        var mask = new MapFoliageMask("item", FoliageFixtures.Owner("owner"), layer.Support, kind, rect);
        var draft = new FoliageLayerDraft(layer);
        if (source == "rule") draft.ClearanceRules = [rule]; else draft.ExceptionMasks = [mask];
        var set = new MapFoliageSet(1, [draft.Build()]);
        var findings = MapFoliageValidation.ValidateFindings(set);
        var finding = Assert.Single(findings);
        Assert.Equal(reason, finding.Reason);
        Assert.Equal(layer.Id, finding.LayerId);
        Assert.Equal("item", finding.EntityId);
        Assert.Equal(0, finding.Index);
        Assert.Equal((source == "rule" ? "ClearanceRules[0]." : "ExceptionMasks[0].") + field, finding.FieldPath);
        if (fault == "kind")
        {
            Assert.Equal(typeof(MapFoliageClearanceKind), finding.EnumType);
            Assert.Equal(99L, finding.ActualInteger);
        }
        else if (reason == MapDocumentRefusal.ValueNonfinite) Assert.False(double.IsFinite(finding.ActualNumber!.Value));
        else
        {
            Assert.Equal(0d, finding.MinimumNumber);
            Assert.Equal(0d, finding.MaximumNumber);
        }
        Assert.Equal(findings.Select(f => f.Message), MapFoliageValidation.Validate(set));
        Assert.Throws<NotSupportedException>(() => ((IList<MapDocumentFinding>)findings).Clear());
        // Strict positive extents have no inclusive finite limit. Repair only the collapsed dimension.
        draft.ClearanceRules = source == "rule" ? [rule with { Kind = MapFoliageClearanceKind.Door, Footprint = new(0, 0, 1, 1) }] : [];
        draft.ExceptionMasks = source == "mask" ? [mask with { Kind = MapFoliageClearanceKind.Door, Footprint = new(0, 0, 1, 1) }] : [];
        Assert.Empty(MapFoliageValidation.ValidateFindings(new(1, [draft.Build()])));
    }

    [Fact]
    public void BoundAndDocumentCollectors_ForwardTheCompleteLocalSnapshot()
    {
        var fixture = FoliageFixtures.Create();
        var draft = new FoliageLayerDraft(fixture.Document.Foliage!.Layers[0]) { RootOffset = float.NaN };
        fixture.Document.Foliage = new(1, [draft.Build()]);
        var local = MapFoliageValidation.ValidateFindings(fixture.Document.Foliage);
        Assert.Single(local);
        var bound = MapFoliageValidation.ValidateBoundFindings(fixture.Document, fixture.Assets);
        Assert.Equal(local, bound);
        Assert.Equal(bound.Select(f => f.Message), MapFoliageValidation.ValidateBound(fixture.Document, fixture.Assets));
        Assert.Throws<NotSupportedException>(() => ((IList<MapDocumentFinding>)bound).Clear());
        var errors = new List<string>();
        var document = new List<MapDocumentFinding>();
        MapFoliageValidation.ValidateDocument(fixture.Document, errors, document);
        Assert.Equal(local, document);
        Assert.Equal(document.Select(f => f.Message), errors);
        draft.RootOffset = 0;
        fixture.Document.Foliage = new(1, [draft.Build()]);
        Assert.Empty(MapFoliageValidation.ValidateBoundFindings(fixture.Document, fixture.Assets));
    }
}
