using System;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageBrushRefusalTests
{
    [Theory]
    [InlineData("layer", MapDocumentRefusal.AuthoredValueMismatch, "LayerId")]
    [InlineData("world-x", MapDocumentRefusal.ValueNonfinite, "WorldX")]
    [InlineData("world-z", MapDocumentRefusal.ValueNonfinite, "WorldZ")]
    [InlineData("radius-nonfinite", MapDocumentRefusal.ValueNonfinite, "RadiusMetres")]
    [InlineData("radius-zero", MapDocumentRefusal.ValueOutsideRange, "RadiusMetres")]
    [InlineData("hardness-nonfinite", MapDocumentRefusal.ValueNonfinite, "Hardness")]
    [InlineData("hardness-low", MapDocumentRefusal.ValueOutsideRange, "Hardness")]
    [InlineData("hardness-high", MapDocumentRefusal.ValueOutsideRange, "Hardness")]
    public void BrushAdmission_ReportsTheActualBranchBeforeChangingDensity(string fault, MapDocumentRefusal reason, string field)
    {
        var layer = FoliageFixtures.BasicLayer();
        var control = new MapPaintFoliage(layer.Id, layer.OriginX, layer.OriginZ, 1, 255, fault == "hardness-low" ? 0 : 1);
        var broken = fault switch
        {
            "layer" => control with { LayerId = "other" },
            "world-x" => control with { WorldX = float.NaN },
            "world-z" => control with { WorldZ = float.PositiveInfinity },
            "radius-nonfinite" => control with { RadiusMetres = float.NaN },
            "radius-zero" => control with { RadiusMetres = 0 },
            "hardness-nonfinite" => control with { Hardness = float.NaN },
            "hardness-low" => control with { Hardness = -1 },
            _ => control with { Hardness = 2 },
        };
        byte[] before = layer.Chunks[0].CopyDensity();
        var error = Assert.Throws<MapDocumentException>(() => MapFoliageBrush.Apply(layer, broken));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(layer.Id, error.LayerId);
        Assert.Equal(field, error.FieldPath);
        if (fault == "layer") Assert.Equal("other", error.ActualText);
        else
        {
            double actual = field switch { "WorldX" => broken.WorldX, "WorldZ" => broken.WorldZ, "RadiusMetres" => broken.RadiusMetres, _ => broken.Hardness };
            Assert.Equal(actual, error.ActualNumber);
            if (field is "Hardness" or "RadiusMetres") Assert.Equal(0d, error.MinimumNumber);
            if (field == "Hardness") Assert.Equal(1d, error.MaximumNumber);
        }
        Assert.Equal(before, layer.Chunks[0].CopyDensity());
        Assert.Equal((byte)255, MapFoliageBrush.Apply(layer, control).DensityAt(0, 0));
    }

    [Theory]
    [InlineData("OriginX", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("OriginZ", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("CellSizeNonfinite", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("CellSize", MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("Width", MapDocumentRefusal.BoundsInvalid)]
    [InlineData("Depth", MapDocumentRefusal.BoundsInvalid)]
    public void RasterAdmission_SelectsItsFirstInvalidScalar(string field, MapDocumentRefusal reason)
    {
        var layer = FoliageFixtures.BasicLayer([]);
        var draft = new FoliageLayerDraft(layer);
        switch (field)
        {
            case "OriginX": draft.OriginX = float.NaN; break;
            case "OriginZ": draft.OriginZ = float.PositiveInfinity; break;
            case "CellSizeNonfinite": draft.CellSize = float.NaN; break;
            case "CellSize": draft.CellSize = 0; break;
            case "Width": draft.Width = 0; break;
            case "Depth": draft.Depth = 0; break;
        }
        var error = Assert.Throws<MapDocumentException>(() => MapFoliageBrush.RequireRaster(draft.Build()));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(layer.Id, error.LayerId);
        Assert.Equal(field == "CellSizeNonfinite" ? "CellSize" : field, error.FieldPath);
        if (field is "Width" or "Depth") { Assert.Equal(0L, error.ActualInteger); Assert.Equal(1L, error.MinimumInteger); }
        else Assert.Equal(field == "OriginX" || field == "CellSizeNonfinite" ? double.NaN : field == "OriginZ" ? double.PositiveInfinity : 0d, error.ActualNumber);
        if (field is "Width" or "Depth")
        {
            draft.Width = field == "Width" ? 1 : draft.Width;
            draft.Depth = field == "Depth" ? 1 : draft.Depth;
        }
        else { draft.OriginX = layer.OriginX; draft.OriginZ = layer.OriginZ; draft.CellSize = layer.CellSize; }
        MapFoliageBrush.RequireRaster(draft.Build());
    }

    [Theory]
    [InlineData(0, 0, 0, 1, MapDocumentRefusal.BoundsInvalid, "Width")]
    [InlineData(0, 0, 1, 0, MapDocumentRefusal.BoundsInvalid, "Depth")]
    [InlineData(-1, 0, 1, 1, MapDocumentRefusal.FoliageRectangleOutOfBounds, "X")]
    [InlineData(0, -1, 1, 1, MapDocumentRefusal.FoliageRectangleOutOfBounds, "Z")]
    [InlineData(35, 0, 1, 1, MapDocumentRefusal.FoliageRectangleOutOfBounds, "X+Width")]
    [InlineData(0, 3, 1, 1, MapDocumentRefusal.FoliageRectangleOutOfBounds, "Z+Depth")]
    public void RectangleAdmission_RetainsTheDomainAndAcceptsTheLastSample(int x, int z, int width, int depth, MapDocumentRefusal reason, string field)
    {
        var layer = FoliageFixtures.BasicLayer([]);
        var error = Assert.Throws<MapDocumentException>(() => MapFoliageBrush.RequireRectangle(layer, x, z, width, depth));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(layer.Id, error.LayerId);
        Assert.Equal(field, error.FieldPath);
        if (reason == MapDocumentRefusal.BoundsInvalid)
        {
            Assert.Equal(width, error.Width);
            Assert.Equal(depth, error.Depth);
            Assert.Equal(0L, error.ActualInteger);
            Assert.Equal(1L, error.MinimumInteger);
        }
        else
        {
            Assert.Equal(x, error.CellX);
            Assert.Equal(z, error.CellZ);
            Assert.Equal(layer.Width, error.Width);
            Assert.Equal(layer.Depth, error.Depth);
        }
        int repairedX = x < 0 ? 0 : x >= layer.Width ? layer.Width - 1 : x;
        int repairedZ = z < 0 ? 0 : z >= layer.Depth ? layer.Depth - 1 : z;
        Assert.Equal((repairedX, repairedZ), MapFoliageBrush.RequireRectangle(layer, repairedX, repairedZ, Math.Max(1, width), Math.Max(1, depth)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RasterBudget_AcceptsExactlyEachIndependentCap(bool chunks)
    {
        var layer = new FoliageLayerDraft(FoliageFixtures.BasicLayer([])) { Width = 65536, Depth = 256 }.Build();
        int width = chunks ? MapFoliageBrush.MaxChunks * MapFoliageChunk.Size : 256;
        int depth = chunks ? 1 : MapFoliageBrush.MaxAddresses / width;
        var error = Assert.Throws<MapDocumentException>(() => MapFoliageBrush.RequireRectangle(layer, 0, 0, width + 1, depth));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        Assert.Equal(layer.Id, error.LayerId);
        Assert.Equal(chunks ? "chunks per raster edit" : "addresses per raster edit", error.Budget);
        Assert.Equal(chunks ? MapFoliageBrush.MaxChunks : MapFoliageBrush.MaxAddresses, error.Limit);
        Assert.Equal(chunks ? MapFoliageBrush.MaxChunks + 1L : (width + 1L) * depth, error.ActualCount);
        Assert.Equal((width - 1, depth - 1), MapFoliageBrush.RequireRectangle(layer, 0, 0, width, depth));
    }

    [Fact]
    public void Rewrite_RefusesAnAdvertisedChunkWithoutMutatingResidentDensity()
    {
        var template = FoliageFixtures.BasicLayer();
        var key = template.Chunks[1].Key;
        var layer = MapFoliageLayer.FromStorage(template, template.ChunkDigests, [template.Chunks[0]]);
        var brush = new MapPaintFoliage(layer.Id, layer.OriginX + 32 * layer.CellSize, layer.OriginZ, 1, 255, 1);
        byte[] before = layer.Chunks[0].CopyDensity();
        var error = Assert.Throws<MapDocumentException>(() => MapFoliageBrush.Apply(layer, brush));
        Assert.Equal(MapDocumentRefusal.FoliageChunkUnavailable, error.Reason);
        Assert.Equal(layer.Id, error.LayerId);
        Assert.Equal(key, error.FoliageChunk);
        Assert.Equal(before, layer.Chunks[0].CopyDensity());
        Assert.Equal((byte)255, MapFoliageBrush.Apply(template, brush).DensityAt(32, 0));
    }
}
