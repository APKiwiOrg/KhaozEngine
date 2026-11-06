using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageLayerRefusalTests
{
    [Theory]
    [InlineData("version", MapDocumentRefusal.SchemaPayloadVersionUnsupported, "PayloadVersion")]
    [InlineData("origin-x", MapDocumentRefusal.ValueNonfinite, "OriginX")]
    [InlineData("origin-z", MapDocumentRefusal.ValueNonfinite, "OriginZ")]
    [InlineData("cell-nonfinite", MapDocumentRefusal.ValueNonfinite, "CellSize")]
    [InlineData("cell-zero", MapDocumentRefusal.ValueOutsideRange, "CellSize")]
    [InlineData("width-low", MapDocumentRefusal.ValueOutsideRange, "Width")]
    [InlineData("width-high", MapDocumentRefusal.ValueOutsideRange, "Width")]
    [InlineData("depth-low", MapDocumentRefusal.ValueOutsideRange, "Depth")]
    [InlineData("depth-high", MapDocumentRefusal.ValueOutsideRange, "Depth")]
    [InlineData("row", MapDocumentRefusal.SchemaEnumUnsupported, "RowDirection")]
    [InlineData("spacing-nonfinite", MapDocumentRefusal.ValueNonfinite, "Spacing")]
    [InlineData("spacing-zero", MapDocumentRefusal.ValueOutsideRange, "Spacing")]
    [InlineData("scale-min-nonfinite", MapDocumentRefusal.ValueNonfinite, "ScaleMin")]
    [InlineData("scale-min-zero", MapDocumentRefusal.ValueOutsideRange, "ScaleMin")]
    [InlineData("scale-max-nonfinite", MapDocumentRefusal.ValueNonfinite, "ScaleMax")]
    [InlineData("scale-order", MapDocumentRefusal.BoundsInvalid, "ScaleMax")]
    [InlineData("root", MapDocumentRefusal.ValueNonfinite, "RootOffset")]
    public void LayerScalars_NameTheFailedBranchAndAcceptItsRepairedBoundary(string fault, MapDocumentRefusal reason, string field)
    {
        var draft = new FoliageLayerDraft(FoliageFixtures.BasicLayer([]));
        switch (fault)
        {
            case "version": draft.PayloadVersion = 2; break;
            case "origin-x": draft.OriginX = float.PositiveInfinity; break;
            case "origin-z": draft.OriginZ = float.NaN; break;
            case "cell-nonfinite": draft.CellSize = float.NaN; break;
            case "cell-zero": draft.CellSize = 0; break;
            case "width-low": draft.Width = 0; break;
            case "width-high": draft.Width = MapFoliagePayloadLimits.MaxExtent + 1; break;
            case "depth-low": draft.Depth = 0; break;
            case "depth-high": draft.Depth = MapFoliagePayloadLimits.MaxExtent + 1; break;
            case "row": draft.RowDirection = (MapRowDirection)99; break;
            case "spacing-nonfinite": draft.Spacing = float.NaN; break;
            case "spacing-zero": draft.Spacing = 0; break;
            case "scale-min-nonfinite": draft.ScaleMin = float.PositiveInfinity; break;
            case "scale-min-zero": draft.ScaleMin = 0; break;
            case "scale-max-nonfinite": draft.ScaleMax = float.NaN; break;
            case "scale-order": draft.ScaleMax = 0.5f; break;
            case "root": draft.RootOffset = float.NegativeInfinity; break;
        }
        var layer = draft.Build();
        var findings = new List<MapDocumentFinding>();
        var errors = MapFoliageValidation.Validate(new(1, [layer]), findings);
        var finding = Assert.Single(findings);
        Assert.Equal(reason, finding.Reason);
        Assert.Equal(layer.Id, finding.LayerId);
        Assert.Equal(field, finding.FieldPath);
        Assert.Equal(new[] { finding.Message }, errors);
        if (fault == "version") { Assert.Equal(2, finding.ActualVersion); Assert.Equal(new[] { 1 }, finding.SupportedVersions); }
        else if (fault == "row") { Assert.Equal(99L, finding.ActualInteger); Assert.Equal(typeof(MapRowDirection), finding.EnumType); }
        else if (field is "Width" or "Depth")
        {
            Assert.Equal(field == "Width" ? layer.Width : layer.Depth, finding.ActualInteger);
            Assert.Equal(1L, finding.MinimumInteger);
            Assert.Equal(MapFoliagePayloadLimits.MaxExtent, finding.MaximumInteger);
        }
        else
        {
            double actual = field switch
            {
                "OriginX" => layer.OriginX, "OriginZ" => layer.OriginZ, "CellSize" => layer.CellSize,
                "Spacing" => layer.Spacing, "ScaleMin" => layer.ScaleMin, "ScaleMax" => layer.ScaleMax, _ => layer.RootOffset,
            };
            Assert.Equal(actual, finding.ActualNumber);
            if (reason == MapDocumentRefusal.ValueOutsideRange) Assert.Equal(0d, finding.MinimumNumber);
            if (reason == MapDocumentRefusal.BoundsInvalid)
            {
                Assert.Equal((double)layer.ScaleMin, finding.MinimumNumber);
                Assert.Equal((double)layer.ScaleMax, finding.MaximumNumber);
            }
        }
        switch (field)
        {
            case "PayloadVersion": draft.PayloadVersion = 1; break;
            case "OriginX": draft.OriginX = 0; break;
            case "OriginZ": draft.OriginZ = 0; break;
            case "RootOffset": draft.RootOffset = 0; break;
            case "CellSize": draft.CellSize = 1; break;
            case "Spacing": draft.Spacing = 1; break;
            case "ScaleMin": draft.ScaleMin = draft.ScaleMax; break;
            case "ScaleMax": draft.ScaleMax = draft.ScaleMin; break;
            case "Width": draft.Width = fault == "width-high" ? MapFoliagePayloadLimits.MaxExtent : 1; break;
            case "Depth": draft.Depth = fault == "depth-high" ? MapFoliagePayloadLimits.MaxExtent : 1; break;
            case "RowDirection": draft.RowDirection = MapRowDirection.PositiveZ; break;
        }
        findings.Clear();
        Assert.Empty(MapFoliageValidation.Validate(new(1, [draft.Build()]), findings));
        Assert.Empty(findings);
    }
}
