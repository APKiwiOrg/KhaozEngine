using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageSetRefusalTests
{
    [Theory]
    [InlineData("version", MapDocumentRefusal.SchemaPayloadVersionUnsupported)]
    [InlineData("empty-id", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("duplicate", MapDocumentRefusal.IdentityDuplicate)]
    [InlineData("layers", MapDocumentRefusal.WorkCapacityExceeded)]
    [InlineData("density", MapDocumentRefusal.WorkCapacityExceeded)]
    public void SetAdmission_CollectsTypedFindingsBesideTheProse(string fault, MapDocumentRefusal reason)
    {
        var layer = FoliageFixtures.BasicLayer([]);
        var control = new MapFoliageSet(1, [layer]);
        MapFoliageSet set = fault switch
        {
            "version" => new(2, [layer]),
            "empty-id" => new(1, [FoliageComparisonFixtures.Change(layer, l => l["id"] = "")]),
            "duplicate" => new(1, [layer, layer]),
            "layers" => new(1, Enumerable.Range(0, MapFoliagePayloadLimits.MaxLayers + 1)
                .Select(i => FoliageComparisonFixtures.Change(layer, l => l["id"] = "layer-" + i)).ToArray()),
            _ => DensitySet(layer),
        };
        var findings = new List<MapDocumentFinding>();
        var errors = MapFoliageValidation.Validate(set, findings);
        var finding = Assert.Single(findings);
        Assert.Equal(reason, finding.Reason);
        Assert.Equal(new[] { finding.Message }, errors);
        if (fault == "version") { Assert.Equal(2, finding.ActualVersion); Assert.Equal(new[] { 1 }, finding.SupportedVersions); }
        if (fault is "empty-id" or "duplicate") Assert.Equal(set.Layers[fault == "duplicate" ? 1 : 0].Id, finding.LayerId);
        if (fault is "layers" or "density")
        {
            Assert.Equal(fault == "layers" ? "layers" : "decoded density bytes per document", finding.Budget);
            Assert.Equal(fault == "layers" ? MapFoliagePayloadLimits.MaxLayers : MapFoliagePayloadLimits.MaxDensityBytes, finding.Limit);
            if (fault == "layers") Assert.Equal(MapFoliagePayloadLimits.MaxLayers + 1, finding.ActualCount);
            else
            {
                Assert.Equal(MapFoliagePayloadLimits.MaxDocumentChunks + 1, finding.ActualCount);
                Assert.Equal(MapFoliagePayloadLimits.MaxDensityBytes + MapFoliageChunk.ByteLength, finding.ActualByteCount);
            }
            control = new(1, set.Layers.Take(set.Layers.Count - 1).ToArray());
        }
        findings.Clear();
        Assert.Empty(MapFoliageValidation.Validate(control, findings));
        Assert.Empty(findings);
    }

    [Fact]
    public void BoundModelAdmission_ReportsTheMissingClosureAsset()
    {
        var fixture = FoliageFixtures.Create();
        var layer = fixture.Document.Foliage!.Layers[0];
        var changed = FoliageComparisonFixtures.Change(layer, l => l["models"]![0]!["assetId"] = "missing");
        fixture.Document.Foliage = new(1, [changed]);
        var findings = new List<MapDocumentFinding>();
        var errors = MapFoliageValidation.ValidateBound(fixture.Document, fixture.Assets, findings);
        var finding = Assert.Single(findings);
        Assert.Equal(MapDocumentRefusal.ReferenceMissing, finding.Reason);
        Assert.Equal("missing", finding.AssetId);
        Assert.Equal(layer.Id, finding.LayerId);
        Assert.Equal("Models[0].AssetId", finding.FieldPath);
        Assert.Equal(new[] { finding.Message }, errors);
        fixture.Document.Foliage = new(1, [layer]);
        findings.Clear();
        Assert.Empty(MapFoliageValidation.ValidateBound(fixture.Document, fixture.Assets, findings));
        Assert.Empty(findings);
    }

    static MapFoliageSet DensitySet(MapFoliageLayer template)
    {
        var rows = Enumerable.Range(0, MapFoliagePayloadLimits.MaxChunksPerLayer)
            .Select(i => new MapFoliageChunkDigest(new(i, 0), new string('a', 64))).ToArray();
        int count = MapFoliagePayloadLimits.MaxDocumentChunks / rows.Length;
        return new(1, Enumerable.Range(0, count + 1).Select(i =>
        {
            var metadata = FoliageComparisonFixtures.Change(template, l =>
            {
                l["id"] = "layer-" + i;
                l["width"] = MapFoliagePayloadLimits.MaxExtent;
                l["depth"] = MapFoliageChunk.Size;
            });
            return MapFoliageLayer.FromStorage(metadata, i < count ? rows : rows.Take(1).ToArray(), []);
        }).ToArray());
    }
}
