using System;
using System.Collections;
using System.Collections.Generic;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageEditValueRefusalTests
{
    sealed record UnsupportedEdit() : MapFoliageEdit("unsupported");

    [Theory]
    [InlineData("nested", MapDocumentRefusal.FoliageNestedSequenceUnsupported)]
    [InlineData("kind", MapDocumentRefusal.OperationKindUnsupported)]
    [InlineData("layer", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("density", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("length", MapDocumentRefusal.SchemaArrayLengthMismatch)]
    [InlineData("low", MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("high", MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("unavailable", MapDocumentRefusal.FoliageChunkUnavailable)]
    public void EditValues_RefuseWithTheirOwnRuleAndLeaveTheSourceUntouched(string fault, MapDocumentRefusal reason)
    {
        var fixture = FoliageFixtures.Create();
        var layer = fixture.Document.Foliage!.Layers[0];
        var density = new MapSetFoliageDensity(layer.Id, 1, 1, 1, 1, [255]);
        var proposed = MapFoliageLayer.FromStorage(layer, [new(new(1, 0), new string('a', 64))], []);
        MapFoliageEdit edit = fault switch
        {
            "nested" => new MapFoliageEditSequence([new MapFoliageEditSequence([], null)], null),
            "kind" => new UnsupportedEdit(),
            "layer" => density with { LayerId = "missing" },
            "density" => density with { Density = null! },
            "length" => density with { Density = [] },
            "low" => density with { Density = [-1] },
            "high" => density with { Density = [256] },
            _ => new MapSetFoliageLayer(proposed),
        };
        var original = fixture.Document.Foliage;
        string before = FoliageStorageFixtures.Snapshot(fixture.Document);
        byte[] bytes = layer.Chunks[0].CopyDensity();
        var error = Assert.Throws<MapDocumentException>(() => MapFoliageEdits.Prepare(fixture.Document, fixture.Assets, edit));
        Assert.Equal(reason, error.Reason);
        if (fault == "kind") Assert.Equal(typeof(UnsupportedEdit), error.RecordType);
        if (fault is not ("kind" or "nested")) Assert.Equal(fault == "layer" ? "missing" : layer.Id, error.LayerId);
        if (fault == "density") Assert.Equal("Density", error.FieldPath);
        if (fault == "length") { Assert.Equal(0L, error.ActualCount); Assert.Equal(1L, error.ExpectedCount); }
        if (fault is "low" or "high")
        {
            Assert.Equal(fault == "low" ? -1L : 256L, error.ActualInteger);
            Assert.Equal(0L, error.MinimumInteger);
            Assert.Equal(255L, error.MaximumInteger);
            Assert.Equal(0, error.Index);
            Assert.Equal(0, error.CellX);
            Assert.Equal(0, error.CellZ);
        }
        if (fault == "unavailable") Assert.Equal(new MapFoliageChunkKey(1, 0), error.FoliageChunk);
        Assert.Same(original, fixture.Document.Foliage);
        Assert.Equal(bytes, layer.Chunks[0].CopyDensity());
        Assert.Equal(before, FoliageStorageFixtures.Snapshot(fixture.Document));
        MapFoliageEdit control = fault switch
        {
            "nested" => new MapFoliageEditSequence([], null),
            "kind" or "unavailable" => new MapSetFoliageLayer(layer),
            "low" => density with { Density = [0] },
            _ => density,
        };
        var accepted = MapFoliageEdits.Prepare(fixture.Document, fixture.Assets, control);
        Assert.NotNull(accepted.After.Foliage);
    }

    [Fact]
    public void DensityLength_ReportsTheCountThatFailedAdmission()
    {
        var fixture = FoliageFixtures.Create();
        var edit = new MapSetFoliageDensity(FoliageFixtures.LayerId, 0, 0, 2, 1, new ChangingCount());
        string before = FoliageStorageFixtures.Snapshot(fixture.Document);
        var error = Assert.Throws<MapDocumentException>(() => MapFoliageEdits.Prepare(fixture.Document, fixture.Assets, edit));
        Assert.Equal(MapDocumentRefusal.SchemaArrayLengthMismatch, error.Reason);
        Assert.Equal(1L, error.ActualCount);
        Assert.Equal(2L, error.ExpectedCount);
        Assert.Equal(before, FoliageStorageFixtures.Snapshot(fixture.Document));
        Assert.NotNull(MapFoliageEdits.Prepare(fixture.Document, fixture.Assets, edit with { Density = [0, 255] }).After.Foliage);
    }

    sealed class ChangingCount : IReadOnlyList<int>
    {
        bool _first = true;
        public int Count { get { int value = _first ? 1 : 2; _first = false; return value; } }
        public int this[int index] => throw new InvalidOperationException();
        public IEnumerator<int> GetEnumerator() => throw new InvalidOperationException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
