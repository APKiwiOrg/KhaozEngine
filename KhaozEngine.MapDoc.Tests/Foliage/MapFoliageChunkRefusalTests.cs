using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageChunkRefusalTests
{
    [Theory]
    [InlineData(-1, 0, "X", -1, 1)]
    [InlineData(2, 0, "X", 2, 1)]
    [InlineData(0, -1, "Z", -1, 0)]
    [InlineData(0, 1, "Z", 1, 0)]
    public void AdvertisedChunkDomain_RetainsTheKeyAndCheckedAxis(int x, int z, string axis, long actual, long maximum)
    {
        var template = FoliageFixtures.BasicLayer([]);
        var key = new MapFoliageChunkKey(x, z);
        var layer = MapFoliageLayer.FromStorage(template, [new(key, new string('a', 64))], []);
        var findings = new List<MapDocumentFinding>();
        var errors = MapFoliageValidation.Validate(new(1, [layer]), findings);
        var finding = Assert.Single(findings);
        Assert.Equal(MapDocumentRefusal.FoliageChunkOutOfDomain, finding.Reason);
        Assert.Equal(layer.Id, finding.LayerId);
        Assert.Equal(key, finding.FoliageChunk);
        Assert.Equal("ChunkDigests[0].Key." + axis, finding.FieldPath);
        Assert.Equal(0, finding.Index);
        Assert.Equal(actual, finding.ActualInteger);
        Assert.Equal(0L, finding.MinimumInteger);
        Assert.Equal(maximum, finding.MaximumInteger);
        Assert.Equal(35, finding.Width);
        Assert.Equal(3, finding.Depth);
        Assert.Equal(new[] { finding.Message }, errors);
        var control = MapFoliageLayer.FromStorage(template, [new(new(x < 0 ? 0 : 1, 0), new string('a', 64))], []);
        findings.Clear();
        Assert.Empty(MapFoliageValidation.Validate(new(1, [control]), findings));
        Assert.Empty(findings);
    }

    [Fact]
    public void LayerChunkCapacity_AcceptsExactlyTheCapWithoutMaterializingDensity()
    {
        var draft = new FoliageLayerDraft(FoliageFixtures.BasicLayer([]))
        { Width = MapFoliagePayloadLimits.MaxExtent, Depth = MapFoliageChunk.Size };
        var rows = Enumerable.Range(0, MapFoliagePayloadLimits.MaxChunksPerLayer + 1)
            .Select(i => new MapFoliageChunkDigest(new(i, 0), new string('a', 64))).ToArray();
        var layer = MapFoliageLayer.FromStorage(draft.Build(), rows, []);
        var findings = new List<MapDocumentFinding>();
        var errors = MapFoliageValidation.Validate(new(1, [layer]), findings);
        var finding = Assert.Single(findings);
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, finding.Reason);
        Assert.Equal(layer.Id, finding.LayerId);
        Assert.Equal("ChunkDigests", finding.FieldPath);
        Assert.Equal("chunks per layer", finding.Budget);
        Assert.Equal(MapFoliagePayloadLimits.MaxChunksPerLayer + 1, finding.ActualCount);
        Assert.Equal(MapFoliagePayloadLimits.MaxChunksPerLayer, finding.Limit);
        Assert.Equal(new[] { finding.Message }, errors);
        var control = MapFoliageLayer.FromStorage(draft.Build(), rows.Take(MapFoliagePayloadLimits.MaxChunksPerLayer).ToArray(), []);
        findings.Clear();
        Assert.Empty(MapFoliageValidation.Validate(new(1, [control]), findings));
        Assert.Empty(findings);
    }
}
