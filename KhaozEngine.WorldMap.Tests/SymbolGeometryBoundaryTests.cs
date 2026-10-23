using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.WorldMap;
using Xunit;

namespace KhaozEngine.Tests.WorldMap;

/// <summary>Custom symbols retain chunk coverage, cache invalidation and authored feature ownership.</summary>
public sealed class SymbolGeometryBoundaryTests
{
    /// <summary>Rotated sized polygons and external details match a larger raster across both signed axes.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(5, false)]
    [InlineData(5, true)]
    public void SymbolGeometry_NeighbourChunksMatchLargerRaster(int level, bool negative)
    {
        float scale = MathF.ScaleB(1f, level);
        var source = Source(scale);
        int first = negative ? -2 : 0;
        source.Footprints.Add(SymbolSizeTests.Footprint(new(0.5f * scale), 0.37f) with
        { Center = new((first + 1) * 8f * scale) });
        var symbol = Symbol(scale);
        var style = SymbolSizeTests.Style(symbol) with
        {
            OutlinePixels = 1.5f,
            BuildingShadow = new(0f, 0f, 0f, 0.5f),
            BuildingShadowOffsetMetres = new(2f * scale, -scale)
        };
        WorldMapChunkImage large = WorldMapRasterizer.Rasterize(source,
            style with { ChunkMetres = 16f }, new(negative ? -1 : 0, negative ? -1 : 0, level));
        for (int y = first; y < first + 2; y++)
            for (int x = first; x < first + 2; x++)
            {
                WorldMapChunkImage small = WorldMapRasterizer.Rasterize(source, style, new(x, y, level));
                for (int row = 0; row < small.Size; row++)
                    for (int column = 0; column < small.Size; column++)
                    {
                        int largeColumn = (x - first) * small.Size + column;
                        int largeRow = (first + 1 - y) * small.Size + row;
                        Assert.Equal(Pixel(large, largeColumn, largeRow), Pixel(small, column, row));
                    }
            }
    }

    /// <summary>Notified edits outside the chunk replace visible custom symbol pixels through sized reach.</summary>
    [Fact]
    public void SymbolGeometry_SourceChangedRebuildsNeighbourPixels()
    {
        var source = Source(1f);
        var symbol = Symbol(1f) with { SizeMetres = new(32f, 8f) };
        var style = SymbolSizeTests.Style(symbol) with { OutlinePixels = 2f };
        using var cache = new WorldMapChunkCache(source, style);
        var coord = new WorldMapChunkCoord(0, 0);
        cache.Request(coord, 0f);
        Assert.True(cache.BuildNext());
        Assert.True(cache.TryGet(coord, out var before));
        source.Footprints.Add(SymbolSizeTests.Footprint(new(0.5f)) with { Center = new(18f, 4f) });
        source.Revision++;

        source.RaiseChanged(new(17.5f, 3.5f, 1f, 1f));

        Assert.False(cache.IsCurrent(coord));
        Assert.True(cache.BuildNext());
        Assert.True(cache.TryGet(coord, out var after));
        Assert.NotEqual(before.Image.Rgba, after.Image.Rgba);
        Assert.Equal(1, after.Image.Revision);
        Assert.True(cache.IsCurrent(coord));
    }

    /// <summary>Raster geometry changes preserve feature anchors and their chunk ownership.</summary>
    [Fact]
    public void SymbolGeometry_FeatureAnchorsAndOwnershipAreUnchanged()
    {
        var source = Source(1f);
        source.Footprints.Add(SymbolSizeTests.Footprint(new(0.5f)) with { Center = new(8f, 4f) });
        var original = SymbolSizeTests.Style(new(WorldMapSymbolKind.Tree, Color.White, Color.Black)) with
        { IconRules = [new(WorldMapFeatureSource.Footprint, ["tree"], "tree", 1)] };
        var updated = original with { SymbolRules = [new(new(), Symbol(1f))] };
        foreach (WorldMapChunkCoord coord in new WorldMapChunkCoord[] { new(0, 0), new(1, 0), new(0, 0, 1) })
        {
            WorldMapChunkFeatures before = WorldMapFeatureCollector.Collect(source, original, coord);
            WorldMapChunkFeatures after = WorldMapFeatureCollector.Collect(source, updated, coord);
            Assert.Equal(before.Icons, after.Icons);
            Assert.Equal(before.Labels, after.Labels);
        }
        WorldMapIconAnchor anchor = Assert.Single(WorldMapFeatureCollector.Collect(source, updated, new(1, 0)).Icons);
        Assert.Equal(new Vector2(8f, 4f), anchor.Position);
    }

    /// <summary>Detail width is fixed in raster pixels as the pyramid level changes.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void SymbolGeometry_DetailWidthIsFixedAcrossLevels(int level)
    {
        float scale = MathF.ScaleB(1f, level);
        var source = Source(scale);
        source.Footprints.Add(SymbolSizeTests.Footprint(new(2f * scale, scale)) with { Center = new(4f * scale) });
        var symbol = new WorldMapSymbol(WorldMapSymbolKind.Bridge, Color.White, Color.Black)
        {
            Geometry = new(SymbolGeometryRasterTests.Rectangle, [new(new(-0.5f, 0f), new(0.5f, 0f))])
        };
        WorldMapChunkImage image = WorldMapRasterizer.Rasterize(source,
            SymbolSizeTests.Style(symbol) with { OutlinePixels = 2f }, new(0, 0, level));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Pixel(image, 16, 15));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Pixel(image, 16, 16));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(image, 16, 14));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(image, 16, 17));
    }

    static WorldMapSymbol Symbol(float scale) => new(WorldMapSymbolKind.Building, Color.White, Color.Black)
    {
        SizeMetres = new(10f * scale, 6f * scale),
        Geometry = new(SymbolGeometryRasterTests.Concave, [new(new(0.5f, 0.25f), new(0.5f, 1f))])
    };

    static TestWorldMapSource Source(float scale)
    {
        var source = new TestWorldMapSource(new(-16f * scale, -16f * scale, 48f * scale, 48f * scale), 4f * scale);
        for (int row = 0; row < 12; row++)
            for (int column = 0; column < 12; column++)
                source.SetCell(column, row, Color.FromHex("#808080"), 0f);
        return source;
    }

    static byte[] Pixel(WorldMapChunkImage image, int column, int row) =>
        image.Rgba.AsSpan((row * image.Size + column) * 4, 4).ToArray();
}
