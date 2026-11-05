using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapNativeFindingTests
{
    [Theory]
    [InlineData("assets-null", "$.nativeAssets", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("asset-null", "$.nativeAssets[0]", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("id", "$.nativeAssets[0].id", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("path", "$.nativeAssets[0].path", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("digest", "$.nativeAssets[0].sha256", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("asset-version", "$.nativeAssets[0].payloadVersion", MapDocumentRefusal.SchemaPayloadVersionUnsupported)]
    [InlineData("resolver-payload", "$.resolverIdentity.payloadVersion", MapDocumentRefusal.SchemaPayloadVersionUnsupported)]
    [InlineData("resolver-version", "resolverIdentity", MapDocumentRefusal.ResolverContractMismatch)]
    public void Metadata_findings_identify_the_failed_branch_without_changing_prose(string fault, string field,
        MapDocumentRefusal reason)
    {
        var doc = NativeResolverFixtures.Create().Document;
        var roots = doc.NativeAssets.ToArray();
        switch (fault)
        {
            case "assets-null": doc.NativeAssets = null!; break;
            case "asset-null": doc.NativeAssets[0] = null!; break;
            case "id": doc.NativeAssets[0] = roots[0] with { Id = "" }; break;
            case "path": doc.NativeAssets[0] = roots[0] with { Path = "" }; break;
            case "digest": doc.NativeAssets[0] = roots[0] with { Sha256 = "" }; break;
            case "asset-version": doc.NativeAssets[0] = roots[0] with { PayloadVersion = 0 }; break;
            case "resolver-payload": doc.ResolverIdentity = new(2, 1); break;
            case "resolver-version": doc.ResolverIdentity = new(1, 3); break;
        }
        var findings = Findings(doc);
        var finding = Assert.Single(findings);
        Assert.Equal(reason, finding.Reason);
        Assert.Equal(field, finding.FieldPath);
        if (fault is "asset-version" or "resolver-payload")
        {
            Assert.Equal(fault == "asset-version" ? 0 : 2, finding.ActualVersion);
            if (fault == "resolver-payload") Assert.Equal(new[] { 1 }, finding.SupportedVersions);
            else Assert.Equal(1, finding.MinimumInteger);
        }
        doc.NativeAssets = roots.ToList();
        doc.ResolverIdentity = new(1, 1);
        Assert.Empty(Findings(doc));
    }

    [Theory]
    [InlineData(0, 1, MapDocumentRefusal.SchemaPayloadVersionUnsupported)]
    [InlineData(1, 0, MapDocumentRefusal.ResolverContractMismatch)]
    public void Nonpositive_identity_versions_keep_both_existing_diagnostics_and_the_failed_branch(int payload, int resolver,
        MapDocumentRefusal reason)
    {
        var doc = NativeResolverFixtures.Create().Document;
        doc.ResolverIdentity = new(payload, resolver);
        var findings = Findings(doc);
        Assert.Equal(2, findings.Count);
        Assert.All(findings, finding => Assert.Equal(reason, finding.Reason));
        Assert.Equal("$.resolverIdentity." + (payload == 0 ? "payloadVersion" : "resolverVersion"), findings[1].FieldPath);
        if (payload == 0)
        {
            Assert.Equal(new[] { 1 }, findings[0].SupportedVersions);
            Assert.Equal(0, findings[1].ActualVersion);
            Assert.Equal(1, findings[1].MinimumInteger);
            Assert.Null(findings[1].SupportedVersions);
        }
        Assert.Equal(new(payload, resolver), doc.ResolverIdentity);
        doc.ResolverIdentity = new(1, 1);
        Assert.Empty(Findings(doc));
    }

    [Fact]
    public void Missing_reference_fields_are_reported_in_predicate_order_and_positive_versions_stay_open()
    {
        var doc = NativeResolverFixtures.Create().Document;
        doc.NativeAssets = [new("", "", "", 2)];
        Assert.Equal("$.nativeAssets[0].id", Assert.Single(Findings(doc)).FieldPath);
        doc.NativeAssets[0] = doc.NativeAssets[0] with { Id = "resource" };
        Assert.Equal("$.nativeAssets[0].path", Assert.Single(Findings(doc)).FieldPath);
        doc.NativeAssets[0] = doc.NativeAssets[0] with { Path = "resource.json" };
        Assert.Equal("$.nativeAssets[0].sha256", Assert.Single(Findings(doc)).FieldPath);
        doc.NativeAssets[0] = doc.NativeAssets[0] with { Sha256 = "nonempty" };
        Assert.Empty(Findings(doc));
    }

    [Theory]
    [InlineData("minX", float.NaN, MapDocumentRefusal.ValueNonfinite)]
    [InlineData("minZ", float.NegativeInfinity, MapDocumentRefusal.ValueNonfinite)]
    [InlineData("maxX", float.PositiveInfinity, MapDocumentRefusal.ValueNonfinite)]
    [InlineData("maxZ", float.NaN, MapDocumentRefusal.ValueNonfinite)]
    [InlineData("maxX", -100, MapDocumentRefusal.BoundsInvalid)]
    [InlineData("maxZ", -100, MapDocumentRefusal.BoundsInvalid)]
    [InlineData("minX", -101, MapDocumentRefusal.BoundsInvalid)]
    public void Playable_bounds_findings_distinguish_finiteness_order_and_containment(string component, float value,
        MapDocumentRefusal reason)
    {
        var doc = NativeResolverFixtures.Create().Document;
        var good = new MapBounds { MinX = -100, MinZ = -100, MaxX = 100, MaxZ = 100 };
        doc.PlayableBounds = new MapBounds
        {
            MinX = component == "minX" ? value : good.MinX,
            MinZ = component == "minZ" ? value : good.MinZ,
            MaxX = component == "maxX" ? value : good.MaxX,
            MaxZ = component == "maxZ" ? value : good.MaxZ,
        };
        var findings = Findings(doc);
        Assert.NotEmpty(findings);
        var first = findings[0];
        Assert.Equal(reason, first.Reason);
        Assert.Equal("$.playableBounds." + component, first.FieldPath);
        Assert.Null(first.ActualNumber);
        if (float.IsFinite(value))
        {
            Assert.Equal(component.EndsWith("X", StringComparison.Ordinal) ? doc.PlayableBounds.MinX : doc.PlayableBounds.MinZ,
                first.MinimumNumber);
            Assert.Equal(component.EndsWith("X", StringComparison.Ordinal) ? doc.PlayableBounds.MaxX : doc.PlayableBounds.MaxZ,
                first.MaximumNumber);
        }
        // -100 and its next float are exact normal binary32 values, so the strict-order control has no cancellation.
        if (component == "maxX" && value == -100) good.MaxX = MathF.BitIncrement(-100);
        if (component == "maxZ" && value == -100) good.MaxZ = MathF.BitIncrement(-100);
        doc.PlayableBounds = good;
        Assert.Empty(Findings(doc));
    }

    [Theory]
    [InlineData("surfaces-null", "$.surfaces", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("surface-id", "$.surfaces.refs[0].id", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("duplicate", "$.surfaces.refs[1].id", MapDocumentRefusal.IdentityDuplicate)]
    [InlineData("frame", "$.surfaces.refs[0].frame", MapDocumentRefusal.BoundsInvalid)]
    [InlineData("digest", "$.surfaces.refs[0].semanticSha256", MapDocumentRefusal.DigestMalformed)]
    [InlineData("span", "$.surfaces.refs[0].indoorSpan", MapDocumentRefusal.BoundsInvalid)]
    public void Surface_metadata_findings_keep_the_surface_and_field(string fault, string field, MapDocumentRefusal reason)
    {
        var doc = NativeResolverFixtures.Create().Document;
        doc.ResolverIdentity = new(1, 2);
        doc.SupportRecipe = MapSupportRecipe.AuthoredBindingsV2;
        var good = Surface();
        doc.Surfaces.Refs.Add(good);
        switch (fault)
        {
            case "surfaces-null": doc.Surfaces = null!; break;
            case "surface-id": doc.Surfaces.Refs[0] = good with { Id = " " }; break;
            case "duplicate": doc.Surfaces.Refs.Add(good); break;
            case "frame": doc.Surfaces.Refs[0] = good with { Frame = good.Frame with { RowDirection = (MapRowDirection)99 } }; break;
            case "digest": doc.Surfaces.Refs[0] = good with { SemanticSha256 = new string('A', 64) }; break;
            case "span": doc.Surfaces.Refs[0] = good with { IndoorSpan = new("", new("room", new("floor", 0, 0)), 0, 1, []) }; break;
        }
        var finding = Assert.Single(Findings(doc));
        Assert.Equal(reason, finding.Reason);
        Assert.Equal(field, finding.FieldPath);
        if (fault is not ("surfaces-null" or "surface-id")) Assert.Equal("floor", finding.SurfaceId);
        doc.Surfaces = new();
        doc.Surfaces.Refs.Add(good);
        Assert.Empty(Findings(doc));
    }

    [Theory]
    [InlineData("explicit-y", "y", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("surface", "surfaceId", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("space", "spaceId", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("referenceY", "referenceY", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("below-nonfinite", "searchBelow", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("above-nonfinite", "searchAbove", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("below-negative", "searchBelow", MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("above-negative", "searchAbove", MapDocumentRefusal.ValueOutsideRange)]
    public void Support_binding_findings_select_the_failed_numeric_or_required_value_branch(string fault, string field,
        MapDocumentRefusal reason)
    {
        var good = new MapSupportBinding(MapSupportBindingKind.Surface, "floor", null, 0, 0, 0);
        var placement = new MapPlacement { Id = "p", SupportBinding = good };
        placement.SupportBinding = fault switch
        {
            "surface" => good with { SurfaceId = null },
            "space" => good with { Kind = MapSupportBindingKind.Space, SurfaceId = null },
            "referenceY" => good with { ReferenceY = float.NaN },
            "below-nonfinite" => good with { SearchBelow = float.NegativeInfinity },
            "above-nonfinite" => good with { SearchAbove = float.NaN },
            "below-negative" => good with { SearchBelow = -1 },
            "above-negative" => good with { SearchAbove = -1 },
            _ => good,
        };
        if (fault == "explicit-y") placement.Y = 0;
        var surfaces = new Dictionary<string, MapSurfaceRef> { ["floor"] = Surface() };
        var errors = new List<string>();
        var findings = new List<MapDocumentFinding>();
        MapNativeValidation.ValidateSupportBinding(placement, surfaces, errors, findings);
        var finding = Assert.Single(findings);
        Assert.Equal(reason, finding.Reason);
        Assert.Equal("p", finding.PlacementId);
        Assert.Equal("$." + (fault == "explicit-y" ? field : "supportBinding." + field), finding.FieldPath);
        Assert.Equal(finding.Message, Assert.Single(errors));
        if (reason == MapDocumentRefusal.ValueOutsideRange)
        {
            Assert.Equal(-1, finding.ActualNumber);
            Assert.Equal(0, finding.MinimumNumber);
        }
        else Assert.Null(finding.ActualNumber);
        placement.Y = null;
        placement.SupportBinding = fault == "space" ? good with { Kind = MapSupportBindingKind.Space, SurfaceId = null, SpaceId = "room" } : good;
        errors.Clear(); findings.Clear();
        MapNativeValidation.ValidateSupportBinding(placement, surfaces, errors, findings);
        Assert.Empty(findings);
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("placement", 0, MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("placement", 2, MapDocumentRefusal.NumericIdHighWaterExceeded)]
    [InlineData("binding", 0, MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("binding", 2, MapDocumentRefusal.NumericIdHighWaterExceeded)]
    [InlineData("transfer", 0, MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("transfer", 2, MapDocumentRefusal.NumericIdHighWaterExceeded)]
    public void Numeric_reservations_identify_the_owner_and_accept_the_exact_limit(string owner, long id, MapDocumentRefusal reason)
    {
        var doc = new MapDocument { NumericIdHighWaterMark = 1 };
        Set(id);
        var finding = Assert.Single(Findings(doc));
        Assert.Equal(reason, finding.Reason);
        Assert.Equal("world", finding.EntityId);
        Assert.Equal(id, finding.NumericId);
        Assert.Equal(1, finding.HighWaterMark);
        Assert.Equal(1, doc.NumericIdHighWaterMark);
        if (reason == MapDocumentRefusal.ValueOutsideRange) Assert.Equal(1, finding.MinimumInteger);
        Set(1);
        Assert.Empty(Findings(doc));

        void Set(long value)
        {
            var binding = new MapPrefabBinding(new(MapPrefabChildKind.Placement, "child"), "world", value, false);
            doc.Placements.Clear();
            doc.Prefabs = null;
            if (owner == "placement") doc.Placements.Add(new() { Id = "world", NumericId = value });
            else if (owner == "binding") doc.Prefabs = new(1, 1, [new("instance", "prefab", "digest", new(new(0, 0, 0), 0, 1), null, new([binding]), [])], []);
            else doc.Prefabs = new(1, 1, [], [new("instance", binding) { Retired = true }]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Duplicate_numeric_ids_and_negative_high_water_have_distinct_findings(bool duplicate)
    {
        var doc = new MapDocument { NumericIdHighWaterMark = duplicate ? 2 : -1 };
        if (duplicate) doc.Placements = [new() { Id = "a", NumericId = 1 }, new() { Id = "b", NumericId = 1 }];
        var finding = Assert.Single(Findings(doc));
        Assert.Equal(duplicate ? MapDocumentRefusal.IdentityDuplicate : MapDocumentRefusal.ValueOutsideRange, finding.Reason);
        Assert.Equal(duplicate ? "b" : null, finding.EntityId);
        if (duplicate) Assert.Equal(1, finding.NumericId);
        else Assert.Equal(0, finding.MinimumInteger);
        Assert.Equal(duplicate ? 2 : -1, doc.NumericIdHighWaterMark);
        if (duplicate) doc.Placements[1].NumericId = 2;
        else doc.NumericIdHighWaterMark = 0;
        Assert.Empty(Findings(doc));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Prefab_numeric_duplicates_retain_the_actual_owner_without_parsing_its_display_text(bool retired)
    {
        var doc = new MapDocument { NumericIdHighWaterMark = 2 };
        var first = new MapPrefabBinding(new(MapPrefabChildKind.Placement, "first"), "first ' quoted", 1, false);
        var second = first with { Child = new(MapPrefabChildKind.Placement, "second"), WorldId = "second ' quoted" };
        Set(second);
        var finding = Assert.Single(Findings(doc));
        Assert.Equal(MapDocumentRefusal.IdentityDuplicate, finding.Reason);
        Assert.Equal(second.WorldId, finding.EntityId);
        Assert.Equal(retired ? "retired transfer" : "prefab binding", finding.SubjectKind);
        Assert.Equal(1, finding.NumericId);
        Assert.Equal(retired ? "$.prefabs.transfers[0].binding.numericId" : "$.prefabs.instances[0].bindings.entries[1].numericId", finding.FieldPath);
        Assert.Equal(2, doc.NumericIdHighWaterMark);
        Set(second with { NumericId = 2 });
        Assert.Empty(Findings(doc));

        void Set(MapPrefabBinding binding) => doc.Prefabs = new(1, 1,
            [new("instance", "prefab", "digest", new(new(0, 0, 0), 0, 1), null, new(retired ? [first] : [first, binding]), [])],
            retired ? [new("instance", binding) { Retired = true }] : []);
    }

    static MapSurfaceRef Surface() => new("floor", MapLatticeFrame.ImportedMetreCentimetre,
        MapSurfaceRole.SupportFloor, MapPresencePolicy.Native, null, null, new string('a', 64));

    static List<MapDocumentFinding> Findings(MapDocument doc)
    {
        var errors = new List<string>();
        var findings = new List<MapDocumentFinding>();
        MapNativeValidation.Validate(doc, errors, findings);
        var prose = new List<string>();
        MapNativeValidation.Validate(doc, prose);
        Assert.Equal(prose, errors);
        Assert.Equal(errors.Count, findings.Count);
        Assert.Equal(findings.Select(f => f.Message), errors.Where(e => findings.Any(f => f.Message == e)));
        return findings;
    }
}
