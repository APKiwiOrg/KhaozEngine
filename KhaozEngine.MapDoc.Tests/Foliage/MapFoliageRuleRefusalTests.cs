using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageRuleRefusalTests
{
    [Theory]
    [InlineData("allowed-order", MapDocumentRefusal.OrderInvalid, "AllowedVisibleMaterials[1]")]
    [InlineData("allowed-duplicate", MapDocumentRefusal.OrderInvalid, "AllowedVisibleMaterials[1]")]
    [InlineData("allowed-missing", MapDocumentRefusal.ReferenceMissing, "AllowedVisibleMaterials[0]")]
    [InlineData("ordinal-id", MapDocumentRefusal.SchemaValueRequired, "CompatibilityOrdinals[0].SurfaceId")]
    [InlineData("ordinal-duplicate", MapDocumentRefusal.IdentityDuplicate, "CompatibilityOrdinals[1].SurfaceId")]
    [InlineData("ordinal-negative", MapDocumentRefusal.ValueOutsideRange, "CompatibilityOrdinals[0].Ordinal")]
    [InlineData("rule-id", MapDocumentRefusal.SchemaValueRequired, "ClearanceRules[0].Id")]
    [InlineData("rule-duplicate", MapDocumentRefusal.IdentityDuplicate, "ClearanceRules[1].Id")]
    [InlineData("rule-asset", MapDocumentRefusal.SchemaValueRequired, "ClearanceRules[0].AssetId")]
    [InlineData("rule-tag", MapDocumentRefusal.SchemaValueRequired, "ClearanceRules[0].PlacementTag")]
    [InlineData("mask-id", MapDocumentRefusal.SchemaValueRequired, "ExceptionMasks[0].Id")]
    [InlineData("mask-duplicate", MapDocumentRefusal.IdentityDuplicate, "ExceptionMasks[1].Id")]
    [InlineData("owner-duplicate", MapDocumentRefusal.IdentityDuplicate, "OwnerSupports[1].Owner")]
    public void CollectionRules_KeepTheirSubjectAndAcceptTheRepairedInput(string fault, MapDocumentRefusal reason, string field)
    {
        var layer = FoliageFixtures.BasicLayer([]);
        var draft = new FoliageLayerDraft(layer);
        var owner = FoliageFixtures.Owner("owner");
        var rule = new MapFoliageClearanceRule("rule", "tree", null, MapFoliageClearanceKind.Door, new(0, 0, 1, 1));
        var mask = new MapFoliageMask("mask", owner, layer.Support, MapFoliageClearanceKind.Door, new(0, 0, 1, 1));
        switch (fault)
        {
            case "allowed-order": draft.AllowedVisibleMaterials = [11, 10]; break;
            case "allowed-duplicate": draft.AllowedVisibleMaterials = [10, 10]; break;
            case "allowed-missing": draft.AllowedVisibleMaterials = [13]; break;
            case "ordinal-id": draft.CompatibilityOrdinals = [new("", 0)]; break;
            case "ordinal-duplicate": draft.CompatibilityOrdinals = [new("floor", 0), new("floor", 1)]; break;
            case "ordinal-negative": draft.CompatibilityOrdinals = [new("floor", -1)]; break;
            case "rule-id": draft.ClearanceRules = [rule with { Id = "" }]; break;
            case "rule-duplicate": draft.ClearanceRules = [rule, rule]; break;
            case "rule-asset": draft.ClearanceRules = [rule with { AssetId = "" }]; break;
            case "rule-tag": draft.ClearanceRules = [rule with { PlacementTag = "" }]; break;
            case "mask-id": draft.ExceptionMasks = [mask with { Id = "" }]; break;
            case "mask-duplicate": draft.ExceptionMasks = [mask, mask]; break;
            case "owner-duplicate": draft.OwnerSupports = [new(owner, layer.Support), new(owner, layer.Support)]; break;
        }
        var findings = new List<MapDocumentFinding>();
        var errors = MapFoliageValidation.Validate(new(1, [draft.Build()]), findings);
        var finding = Assert.Single(findings);
        Assert.Equal(reason, finding.Reason);
        Assert.Equal(layer.Id, finding.LayerId);
        Assert.Equal(field, finding.FieldPath);
        Assert.Equal(fault is "allowed-order" or "allowed-duplicate" || fault.EndsWith("duplicate", StringComparison.Ordinal) ? 1 : 0, finding.Index);
        if (fault.StartsWith("allowed", StringComparison.Ordinal))
        {
            Assert.Equal(fault == "allowed-missing" ? 13L : 10L, finding.ActualInteger);
            if (reason == MapDocumentRefusal.OrderInvalid) Assert.Equal(fault == "allowed-order" ? 11L : 10L, finding.PreviousInteger);
        }
        if (fault.StartsWith("ordinal", StringComparison.Ordinal))
        {
            Assert.Equal(fault == "ordinal-id" ? "" : "floor", finding.SurfaceId);
            if (fault == "ordinal-negative") { Assert.Equal(-1L, finding.ActualInteger); Assert.Equal(0L, finding.MinimumInteger); }
        }
        if (fault.StartsWith("rule", StringComparison.Ordinal))
        {
            Assert.Equal(fault == "rule-id" ? "" : "rule", finding.EntityId);
            if (fault == "rule-asset") Assert.Equal("", finding.AssetId);
            if (fault == "rule-tag") Assert.Equal("", finding.ActualText);
        }
        if (fault.StartsWith("mask", StringComparison.Ordinal)) Assert.Equal(fault == "mask-id" ? "" : "mask", finding.EntityId);
        if (fault == "owner-duplicate")
        {
            Assert.Equal(owner.Id, finding.EntityId);
            Assert.Equal(owner.Kind, finding.ChildKind);
            Assert.Equal(owner.Scope, finding.ReferenceScope);
        }
        Assert.Equal(new[] { finding.Message }, errors);
        draft.AllowedVisibleMaterials = layer.AllowedVisibleMaterials;
        draft.CompatibilityOrdinals = layer.CompatibilityOrdinals;
        if (fault.StartsWith("rule", StringComparison.Ordinal)) draft.ClearanceRules = [rule];
        if (fault.StartsWith("mask", StringComparison.Ordinal)) draft.ExceptionMasks = [mask];
        if (fault == "owner-duplicate") draft.OwnerSupports = [new(owner, layer.Support)];
        findings.Clear();
        Assert.Empty(MapFoliageValidation.Validate(new(1, [draft.Build()]), findings));
        Assert.Empty(findings);
    }

    [Theory]
    [InlineData("rules")]
    [InlineData("masks")]
    [InlineData("owners")]
    public void CollectionCapacity_ReportsTheBudgetAndAcceptsExactlyTheCap(string collection)
    {
        var layer = FoliageFixtures.BasicLayer([]);
        int limit = collection switch
        {
            "rules" => MapFoliagePayloadLimits.MaxClearanceRules,
            "masks" => MapFoliagePayloadLimits.MaxExceptionMasks,
            _ => MapFoliagePayloadLimits.MaxOwnerSupports,
        };
        var draft = new FoliageLayerDraft(layer);
        if (collection == "rules") draft.ClearanceRules = Enumerable.Range(0, limit + 1)
            .Select(i => new MapFoliageClearanceRule("rule-" + i, "tree", null, MapFoliageClearanceKind.Door, new(0, 0, 1, 1))).ToArray();
        if (collection == "masks") draft.ExceptionMasks = Enumerable.Range(0, limit + 1)
            .Select(i => new MapFoliageMask("mask-" + i, FoliageFixtures.Owner("owner"), layer.Support, MapFoliageClearanceKind.Door, new(0, 0, 1, 1))).ToArray();
        if (collection == "owners") draft.OwnerSupports = Enumerable.Range(0, limit + 1)
            .Select(i => new MapFoliageOwnerSupport(FoliageFixtures.Owner("owner-" + i), layer.Support)).ToArray();
        var findings = new List<MapDocumentFinding>();
        var errors = MapFoliageValidation.Validate(new(1, [draft.Build()]), findings);
        var finding = Assert.Single(findings);
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, finding.Reason);
        Assert.Equal(layer.Id, finding.LayerId);
        Assert.Equal(collection switch { "rules" => "ClearanceRules", "masks" => "ExceptionMasks", _ => "OwnerSupports" }, finding.FieldPath);
        Assert.Equal(collection switch { "rules" => "clearance rules", "masks" => "exception masks", _ => "owner supports" }, finding.Budget);
        Assert.Equal(limit + 1, finding.ActualCount);
        Assert.Equal(limit, finding.Limit);
        Assert.Equal(new[] { finding.Message }, errors);
        if (collection == "rules") draft.ClearanceRules = draft.ClearanceRules.Take(limit).ToArray();
        if (collection == "masks") draft.ExceptionMasks = draft.ExceptionMasks.Take(limit).ToArray();
        if (collection == "owners") draft.OwnerSupports = draft.OwnerSupports.Take(limit).ToArray();
        findings.Clear();
        Assert.Empty(MapFoliageValidation.Validate(new(1, [draft.Build()]), findings));
        Assert.Empty(findings);
    }
}
