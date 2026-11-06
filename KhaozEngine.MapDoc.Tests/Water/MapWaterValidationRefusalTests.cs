using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Water;

public sealed class MapWaterValidationRefusalTests
{
    [Theory]
    [InlineData("null", MapDocumentRefusal.SchemaValueRequired, "Media")]
    [InlineData("key", MapDocumentRefusal.SchemaValueRequired, "Key")]
    [InlineData("duplicate", MapDocumentRefusal.IdentityDuplicate, "Key")]
    [InlineData("kind", MapDocumentRefusal.SchemaEnumUnsupported, "Kind")]
    [InlineData("nonfinite", MapDocumentRefusal.ValueNonfinite, "WadeSpeedScale")]
    [InlineData("low", MapDocumentRefusal.ValueOutsideRange, "WadeSpeedScale")]
    [InlineData("high", MapDocumentRefusal.ValueOutsideRange, "WadeSpeedScale")]
    public void Medium_ReportsOneDefectAndAcceptsTheValidLimit(string defect, MapDocumentRefusal reason, string field)
    {
        var doc = WaterFixtures.OceanOverCaves();
        var valid = new MapWaterMediumDoc("unused", MapWaterMediumKind.Water, 1f);
        var medium = defect switch
        {
            "null" => null!,
            "key" => valid with { Key = " " },
            "duplicate" => valid with { Key = doc.Water!.Media[0].Key },
            "kind" => valid with { Kind = (MapWaterMediumKind)255 },
            "nonfinite" => valid with { WadeSpeedScale = float.NaN },
            "low" => valid with { WadeSpeedScale = 0 },
            _ => valid with { WadeSpeedScale = 1.25f },
        };
        doc.Water!.Media.Add(medium);
        var before = doc.Water.Media.ToArray();
        var finding = Assert.Single(Findings(doc));
        Assert.Equal(reason, finding.Reason);
        Assert.Equal(field, finding.FieldPath);
        if (medium is not null) Assert.Equal(medium.Key, finding.EntityId);
        if (defect == "kind")
        {
            Assert.Equal(255, finding.ActualInteger);
            Assert.Equal(typeof(MapWaterMediumKind), finding.EnumType);
        }
        if (defect is "low" or "high")
        {
            Assert.Equal((double)medium!.WadeSpeedScale, finding.ActualNumber);
            Assert.Equal(0d, finding.MinimumNumber);
            Assert.Equal(1d, finding.MaximumNumber);
        }
        if (defect == "nonfinite") Assert.True(double.IsNaN(finding.ActualNumber!.Value));
        Assert.Equal(before, doc.Water.Media);
        doc.Water.Media[^1] = valid;
        Assert.Empty(Findings(doc));
    }

    [Theory]
    [InlineData("null", MapDocumentRefusal.SchemaValueRequired, "Bodies")]
    [InlineData("id", MapDocumentRefusal.SchemaValueRequired, "Id")]
    [InlineData("duplicate", MapDocumentRefusal.IdentityDuplicate, "Id")]
    [InlineData("nonfinite", MapDocumentRefusal.ValueNonfinite, "SurfaceY")]
    [InlineData("far", MapDocumentRefusal.ValueOutsideRange, "SurfaceY")]
    [InlineData("exact", MapDocumentRefusal.GeometryNotRepresentable, "SurfaceY")]
    [InlineData("medium", MapDocumentRefusal.ReferenceMissing, "Medium")]
    [InlineData("null-medium", MapDocumentRefusal.SchemaValueRequired, "Medium")]
    [InlineData("material", MapDocumentRefusal.SchemaValueRequired, "RenderMaterial")]
    public void Body_ReportsOneDefectAndAcceptsTheCorrectedBody(string defect, MapDocumentRefusal reason, string field)
    {
        var doc = WaterFixtures.OceanOverCaves();
        int index = doc.Water!.Bodies.FindIndex(b => b.Id == "ocean");
        var valid = doc.Water.Bodies[index];
        var body = defect switch
        {
            "null" => null!,
            "id" => valid with { Id = " " },
            "duplicate" => valid,
            "nonfinite" => valid with { SurfaceY = float.PositiveInfinity },
            "far" => valid with { SurfaceY = 1_000_001 },
            // This normal float needs a denominator beyond the bounded exact-value representation.
            "exact" => valid with { SurfaceY = 1e-20f },
            "medium" => valid with { Medium = "absent" },
            "null-medium" => valid with { Medium = null! },
            _ => valid with { RenderMaterial = " " },
        };
        if (defect == "duplicate") doc.Water.Bodies.Add(body);
        else doc.Water.Bodies[index] = body;
        var before = doc.Water.Bodies.ToArray();
        var finding = Assert.Single(Findings(doc));
        Assert.Equal(reason, finding.Reason);
        Assert.Equal(field, finding.FieldPath);
        if (body is not null) Assert.Equal(body.Id, finding.EntityId);
        if (defect is "nonfinite" or "exact")
        {
            var containment = MapWaterContainment.Validate(KhaozEngine.MapDoc.Storage.MapScopedSurfaces.CompleteView(doc.Surfaces), doc.Water);
            var level = Assert.Single(containment.Findings, f => f.EntityId == body!.Id && f.FieldPath == "SurfaceY");
            Assert.Equal(reason, level.Reason);
            Assert.Equal((double)body!.SurfaceY, level.ActualNumber);
        }
        if (defect == "far")
        {
            Assert.Equal(1_000_001d, finding.ActualNumber);
            Assert.Equal(-1_000_000d, finding.MinimumNumber);
            Assert.Equal(1_000_000d, finding.MaximumNumber);
            valid = valid with { SurfaceY = 1_000_000 };
        }
        if (defect == "medium") Assert.Equal("absent", finding.ActualText);
        Assert.Equal(before, doc.Water.Bodies);
        if (defect == "duplicate") doc.Water.Bodies.RemoveAt(doc.Water.Bodies.Count - 1);
        else doc.Water.Bodies[index] = valid;
        Assert.Empty(Findings(doc));
        if (defect is "nonfinite" or "exact")
            Assert.DoesNotContain(MapWaterContainment.Validate(KhaozEngine.MapDoc.Storage.MapScopedSurfaces.CompleteView(doc.Surfaces), doc.Water).Findings,
                f => f.EntityId == valid.Id && f.FieldPath == "SurfaceY");
    }

    static IReadOnlyList<MapDocumentFinding> Findings(MapDocument doc)
    {
        var errors = new List<string>();
        var findings = new List<MapDocumentFinding>();
        MapWaterValidation.Validate(doc, errors, new MapWaterWorkBudget(), findings);
        Assert.Equal(errors, findings.Select(f => f.Message));
        return findings;
    }
}
