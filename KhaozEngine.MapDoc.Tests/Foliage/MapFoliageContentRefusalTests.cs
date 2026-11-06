using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageContentRefusalTests
{
    [Theory]
    [InlineData("empty", MapDocumentRefusal.ValueOutsideRange, "Models")]
    [InlineData("capacity", MapDocumentRefusal.WorkCapacityExceeded, "Models")]
    [InlineData("id", MapDocumentRefusal.SchemaValueRequired, "Models[0].AssetId")]
    [InlineData("duplicate", MapDocumentRefusal.IdentityDuplicate, "Models[1].AssetId")]
    [InlineData("weight-nonfinite", MapDocumentRefusal.ValueNonfinite, "Models[0].Weight")]
    [InlineData("weight-zero", MapDocumentRefusal.ValueOutsideRange, "Models[0].Weight")]
    [InlineData("total", MapDocumentRefusal.WorkCapacityExceeded, "Models.Weight")]
    public void Models_KeepTheRuleAndAssetContextWithRepairedControls(string fault, MapDocumentRefusal reason, string field)
    {
        var draft = new FoliageLayerDraft(FoliageFixtures.BasicLayer([]));
        var valid = draft.Models;
        draft.Models = fault switch
        {
            "empty" => [],
            "capacity" => Enumerable.Range(0, MapFoliagePayloadLimits.MaxModels + 1).Select(i => new MapFoliageModel("model-" + i, 1)).ToArray(),
            "id" => [new("", 1)],
            "duplicate" => [new("reed-a", 1), new("reed-a", 1)],
            "weight-nonfinite" => [new("reed-a", float.NaN)],
            "weight-zero" => [new("reed-a", 0)],
            _ => [new("reed-a", float.MaxValue), new("reed-b", float.MaxValue)],
        };
        var findings = new List<MapDocumentFinding>();
        var errors = MapFoliageValidation.Validate(new(1, [draft.Build()]), findings);
        Assert.Equal(fault == "weight-nonfinite" ? 2 : 1, findings.Count);
        var finding = Assert.Single(findings, f => f.FieldPath == field);
        Assert.Equal(reason, finding.Reason);
        Assert.Equal(FoliageFixtures.LayerId, finding.LayerId);
        Assert.Equal(errors, findings.Select(f => f.Message));
        if (field == "Models")
        {
            Assert.Equal(draft.Models.Count, finding.ActualCount);
            if (fault == "empty") Assert.Equal(1L, finding.MinimumInteger);
            else { Assert.Equal("models", finding.Budget); Assert.Equal(MapFoliagePayloadLimits.MaxModels, finding.Limit); }
        }
        else if (fault == "total")
        {
            Assert.True(double.IsPositiveInfinity(finding.ActualNumber!.Value));
            Assert.Equal("model weights", finding.Budget);
            Assert.Equal((double)float.MaxValue, finding.MaximumNumber);
        }
        else
        {
            int index = fault == "duplicate" ? 1 : 0;
            Assert.Equal(index, finding.Index);
            Assert.Equal(draft.Models[index].AssetId, finding.AssetId);
            if (field.EndsWith("Weight", StringComparison.Ordinal))
            {
                Assert.Equal((double)draft.Models[index].Weight, finding.ActualNumber);
                Assert.Equal(0d, finding.MinimumNumber);
            }
        }
        draft.Models = fault switch
        {
            "empty" => [new("reed-a", 1)],
            "capacity" => draft.Models.Take(MapFoliagePayloadLimits.MaxModels).ToArray(),
            // Two finite half-max weights sum exactly to float.MaxValue, without cancellation or subnormals.
            "total" => [new("reed-a", float.MaxValue / 2), new("reed-b", float.MaxValue / 2)],
            _ => valid,
        };
        findings.Clear();
        Assert.Empty(MapFoliageValidation.Validate(new(1, [draft.Build()]), findings));
        Assert.Empty(findings);
    }

    [Theory]
    [InlineData("materials", MapDocumentRefusal.WorkCapacityExceeded, "Materials")]
    [InlineData("allowed", MapDocumentRefusal.WorkCapacityExceeded, "AllowedVisibleMaterials")]
    [InlineData("zero", MapDocumentRefusal.ValueOutsideRange, "Materials[2].Id")]
    [InlineData("duplicate", MapDocumentRefusal.IdentityDuplicate, "Materials[2].Id")]
    [InlineData("resource", MapDocumentRefusal.SchemaValueRequired, "Materials[2].ResourceId")]
    public void Materials_KeepTheirDeclaredIdentityAndCapacity(string fault, MapDocumentRefusal reason, string field)
    {
        var draft = new FoliageLayerDraft(FoliageFixtures.BasicLayer([]));
        var valid = draft.Materials;
        var allowed = draft.AllowedVisibleMaterials;
        if (fault is "materials" or "allowed")
        {
            draft.Materials = Enumerable.Range(1, MapFoliagePayloadLimits.MaxMaterials)
                .Select(i => new MapFoliageMaterial((ushort)i, "material", MapFoliageMaterialKind.Ordinary)).ToArray();
            if (fault == "materials") draft.Materials = draft.Materials.Append(draft.Materials[0]).ToArray();
            else draft.AllowedVisibleMaterials = Enumerable.Range(1, MapFoliagePayloadLimits.MaxMaterials)
                .Select(i => (ushort)i).Append((ushort)1).ToArray();
        }
        else
        {
            var materials = draft.Materials.ToArray();
            materials[2] = fault switch
            {
                "zero" => materials[2] with { Id = 0 },
                "duplicate" => materials[2] with { Id = 10 },
                _ => materials[2] with { ResourceId = "" },
            };
            draft.Materials = materials;
        }
        var findings = new List<MapDocumentFinding>();
        var errors = MapFoliageValidation.Validate(new(1, [draft.Build()]), findings);
        // A collection above the entire ushort nonzero domain necessarily repeats an identity.
        Assert.Equal(fault == "materials" ? 2 : 1, findings.Count);
        var finding = findings[0];
        Assert.Equal(reason, finding.Reason);
        Assert.Equal(FoliageFixtures.LayerId, finding.LayerId);
        Assert.Equal(field, finding.FieldPath);
        if (fault is "materials" or "allowed")
        {
            Assert.Equal(MapFoliagePayloadLimits.MaxMaterials + 1, finding.ActualCount);
            Assert.Equal(MapFoliagePayloadLimits.MaxMaterials, finding.Limit);
            Assert.Equal(fault == "materials" ? "materials" : "allowed visible materials", finding.Budget);
        }
        else
        {
            Assert.Equal(2, finding.Index);
            Assert.Equal(draft.Materials[2].Id, finding.ActualInteger);
            Assert.Equal(draft.Materials[2].ResourceId, finding.ResourceId);
            if (fault == "zero") Assert.Equal(1L, finding.MinimumInteger);
        }
        // W6-F1-05 still owns the allowed-list ordering finding, which remains prose for now.
        Assert.Equal(fault == "allowed" ? findings.Count + 1 : findings.Count, errors.Count);
        if (fault == "materials") draft.Materials = draft.Materials.Take(MapFoliagePayloadLimits.MaxMaterials).ToArray();
        else if (fault == "allowed") draft.AllowedVisibleMaterials = draft.AllowedVisibleMaterials.Take(MapFoliagePayloadLimits.MaxMaterials).ToArray();
        else
        {
            draft.Materials = fault == "zero" ? [valid[0], valid[1], valid[2] with { Id = 1 }] : valid;
            draft.AllowedVisibleMaterials = allowed;
        }
        findings.Clear();
        Assert.Empty(MapFoliageValidation.Validate(new(1, [draft.Build()]), findings));
        Assert.Empty(findings);
    }
}
