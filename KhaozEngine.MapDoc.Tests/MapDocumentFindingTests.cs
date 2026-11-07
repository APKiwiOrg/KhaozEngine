using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapDocumentFindingTests
{
    [Theory]
    [InlineData("version", MapDocumentRefusal.DocumentVersionUnsupported, "$.formatVersion")]
    [InlineData("id", MapDocumentRefusal.SchemaValueRequired, "$.id")]
    [InlineData("bounds-x", MapDocumentRefusal.BoundsInvalid, "$.bounds.maxX")]
    [InlineData("bounds-z", MapDocumentRefusal.BoundsInvalid, "$.bounds.maxZ")]
    [InlineData("bounds-nonfinite", MapDocumentRefusal.ValueNonfinite, "$.bounds.maxX")]
    [InlineData("scatter-name", MapDocumentRefusal.SchemaValueRequired, "$.scatterLayers[1].name")]
    [InlineData("scatter-duplicate", MapDocumentRefusal.IdentityDuplicate, "$.scatterLayers[1].name")]
    [InlineData("scatter-cell", MapDocumentRefusal.ValueOutsideRange, "$.scatterLayers[1].cellSize")]
    [InlineData("scatter-scale", MapDocumentRefusal.ValueOutsideRange, "$.scatterLayers[1].scaleMax")]
    [InlineData("scatter-cell-nonfinite", MapDocumentRefusal.ValueNonfinite, "$.scatterLayers[1].cellSize")]
    [InlineData("scatter-scale-nonfinite", MapDocumentRefusal.ValueNonfinite, "$.scatterLayers[1].scaleMin")]
    [InlineData("companion-name", MapDocumentRefusal.SchemaValueRequired, "$.companionLayers[1].name")]
    [InlineData("companion-duplicate", MapDocumentRefusal.IdentityDuplicate, "$.companionLayers[1].name")]
    [InlineData("companion-host", MapDocumentRefusal.ReferenceMissing, "$.companionLayers[1].hostLayer")]
    [InlineData("companion-count", MapDocumentRefusal.ValueOutsideRange, "$.companionLayers[1].countMax")]
    [InlineData("exclusion-shape", MapDocumentRefusal.SchemaValueRequired, "$.exclusions[1].shape")]
    [InlineData("exclusion-duplicate", MapDocumentRefusal.IdentityDuplicate, "$.exclusions[1].name")]
    [InlineData("override-shape", MapDocumentRefusal.SchemaValueRequired, "$.scatterOverrides[1].shape")]
    [InlineData("override-density", MapDocumentRefusal.ValueOutsideRange, "$.scatterOverrides[1].densityMultiplier")]
    [InlineData("override-nonfinite", MapDocumentRefusal.ValueNonfinite, "$.scatterOverrides[1].densityMultiplier")]
    [InlineData("override-duplicate", MapDocumentRefusal.IdentityDuplicate, "$.scatterOverrides[1].name")]
    [InlineData("region-name", MapDocumentRefusal.SchemaValueRequired, "$.regions[1].name")]
    [InlineData("region-duplicate", MapDocumentRefusal.IdentityDuplicate, "$.regions[1].name")]
    public void StructuralFinding_NamesItsRuleAndSubjectBesideARepairedControl(string fault, MapDocumentRefusal reason, string path)
    {
        var doc = ValidDocument();
        Action repair = Break(doc, fault);
        var findings = new List<MapDocumentFinding>();
        var errors = MapDocumentValidator.Validate(doc, MapDocRegistry.CreateDefault(), findings);
        var finding = Assert.Single(findings);
        Assert.Single(errors);
        Assert.Equal(reason, finding.Reason);
        Assert.Equal(path, finding.FieldPath);
        Assert.Equal(errors, findings.Select(f => f.Message));
        if (fault == "version")
        {
            Assert.Equal(0, finding.ActualVersion);
            Assert.Equal(new[] { MapDocumentFile.CurrentFormatVersion }, finding.SupportedVersions);
        }
        else if (fault is "bounds-x" or "bounds-z")
        {
            Assert.Equal(-100d, finding.MinimumNumber);
            Assert.Equal(-100d, finding.MaximumNumber);
        }
        else if (fault is not ("id" or "bounds-nonfinite"))
        {
            Assert.Equal(1, finding.Index);
            Assert.Equal(fault.EndsWith("name", StringComparison.Ordinal) ? "" : fault.EndsWith("duplicate", StringComparison.Ordinal) ? "first" : "second", finding.EntityId);
            Assert.NotNull(finding.SubjectKind);
        }
        if (fault == "companion-host") Assert.Equal("missing", finding.LayerId);
        if (reason == MapDocumentRefusal.ValueNonfinite) Assert.Null(finding.ActualNumber);
        if (fault is "scatter-cell" or "override-density")
        {
            Assert.Equal(fault == "scatter-cell" ? 0d : -1d, finding.ActualNumber);
            Assert.Equal(0d, finding.MinimumNumber);
        }
        if (fault == "scatter-scale")
        {
            Assert.Equal(0d, finding.ActualNumber);
            Assert.Equal(1d, finding.MinimumNumber);
        }
        if (fault == "companion-count")
        {
            Assert.Equal(0L, finding.ActualInteger);
            Assert.Equal(1L, finding.MinimumInteger);
        }
        repair();
        findings.Clear();
        Assert.Empty(MapDocumentValidator.Validate(doc, MapDocRegistry.CreateDefault(), findings));
        Assert.Empty(findings);
    }

    internal static MapDocument ValidDocument()
    {
        var doc = new MapDocument { Id = "legacy", Bounds = new() { MinX = -100, MinZ = -100, MaxX = 100, MaxZ = 100 } };
        foreach (string name in new[] { "first", "second" })
        {
            doc.ScatterLayers.Add(new() { Name = name, CellSize = 1, ScaleMin = 1, ScaleMax = 1 });
            doc.CompanionLayers.Add(new() { Name = name, HostLayer = "first", CountMin = 1, CountMax = 1 });
            doc.Exclusions.Add(new() { Name = name, Shape = new DiscShapeDoc() });
            doc.ScatterOverrides.Add(new() { Name = name, Shape = new DiscShapeDoc(), DensityMultiplier = 0 });
            doc.Regions.Add(new() { Name = name, Shape = new DiscShapeDoc() });
        }
        return doc;
    }

    static Action Break(MapDocument doc, string fault)
    {
        switch (fault)
        {
            case "version": doc.FormatVersion = 0; return () => doc.FormatVersion = MapDocumentFile.CurrentFormatVersion;
            case "id": doc.Id = ""; return () => doc.Id = "legacy";
            case "bounds-x": doc.Bounds.MaxX = -100; return () => doc.Bounds.MaxX = 100;
            case "bounds-z": doc.Bounds.MaxZ = -100; return () => doc.Bounds.MaxZ = 100;
            case "bounds-nonfinite": doc.Bounds.MaxX = float.NaN; return () => doc.Bounds.MaxX = 100;
            case "scatter-name": doc.ScatterLayers[1].Name = ""; return () => doc.ScatterLayers[1].Name = "second";
            case "scatter-duplicate": doc.ScatterLayers[1].Name = "first"; return () => doc.ScatterLayers[1].Name = "second";
            case "scatter-cell": doc.ScatterLayers[1].CellSize = 0; return () => doc.ScatterLayers[1].CellSize = 1;
            case "scatter-scale": doc.ScatterLayers[1].ScaleMax = 0; return () => doc.ScatterLayers[1].ScaleMax = 1;
            case "scatter-cell-nonfinite": doc.ScatterLayers[1].CellSize = float.NegativeInfinity; return () => doc.ScatterLayers[1].CellSize = 1;
            case "scatter-scale-nonfinite": doc.ScatterLayers[1].ScaleMin = float.PositiveInfinity; return () => doc.ScatterLayers[1].ScaleMin = 1;
            case "companion-name": doc.CompanionLayers[1].Name = ""; return () => doc.CompanionLayers[1].Name = "second";
            case "companion-duplicate": doc.CompanionLayers[1].Name = "first"; return () => doc.CompanionLayers[1].Name = "second";
            case "companion-host": doc.CompanionLayers[1].HostLayer = "missing"; return () => doc.CompanionLayers[1].HostLayer = "first";
            case "companion-count": doc.CompanionLayers[1].CountMax = 0; return () => doc.CompanionLayers[1].CountMax = 1;
            case "exclusion-shape": doc.Exclusions[1].Shape = null; return () => doc.Exclusions[1].Shape = new DiscShapeDoc();
            case "exclusion-duplicate": doc.Exclusions[1].Name = "first"; return () => doc.Exclusions[1].Name = "second";
            case "override-shape": doc.ScatterOverrides[1].Shape = null; return () => doc.ScatterOverrides[1].Shape = new DiscShapeDoc();
            case "override-density": doc.ScatterOverrides[1].DensityMultiplier = -1; return () => doc.ScatterOverrides[1].DensityMultiplier = 0;
            case "override-nonfinite": doc.ScatterOverrides[1].DensityMultiplier = float.NegativeInfinity; return () => doc.ScatterOverrides[1].DensityMultiplier = 0;
            case "override-duplicate": doc.ScatterOverrides[1].Name = "first"; return () => doc.ScatterOverrides[1].Name = "second";
            case "region-name": doc.Regions[1].Name = ""; return () => doc.Regions[1].Name = "second";
            case "region-duplicate": doc.Regions[1].Name = "first"; return () => doc.Regions[1].Name = "second";
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }
    }
}
