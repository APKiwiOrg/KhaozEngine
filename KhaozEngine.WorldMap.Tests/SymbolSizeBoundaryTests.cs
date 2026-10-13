using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.WorldMap;
using KhaozEngine.WorldMap.Raster;
using Xunit;

namespace KhaozEngine.Tests.WorldMap;

/// <summary>Sized symbols reach neighbouring chunks without changing feature ownership or retention.</summary>
public sealed class SymbolSizeBoundaryTests
{
    /// <summary>Neighbour-only footprints paint continuous crowns and rotated rectangles on both axes at every fixture level.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void SizedSymbols_AdjacentChunksMatchLargerRaster(int level)
    {
        float scale = MathF.ScaleB(1f, level);
        float side = 8f * scale;
        foreach (WorldMapSymbolKind kind in new[] { WorldMapSymbolKind.Tree, WorldMapSymbolKind.Building })
            foreach (bool north in new[] { false, true })
            {
                var source = SymbolSizeTests.Source();
                float offset = (kind == WorldMapSymbolKind.Tree ? 2.25f : 4.5f) * scale;
                Vector2 center = north ? new(side / 2f, side + offset) : new(side + offset, side / 2f);
                source.Footprints.Add(SymbolSizeTests.Footprint(new(0.5f * scale),
                    kind == WorldMapSymbolKind.Tree ? 0f : MathF.PI / 4f) with
                { Center = center });
                var style = SymbolSizeTests.Style(SymbolSizeTests.Sized(kind, new(8f * scale))) with
                {
                    BuildingShadow = Color.Black,
                    BuildingShadowOffsetMetres = new(2f * scale, -scale)
                };
                WorldMapChunkImage large = WorldMapRasterizer.Rasterize(source,
                    style with { ChunkMetres = 16f }, new(0, 0, level));
                for (int neighbour = 0; neighbour < 2; neighbour++)
                {
                    WorldMapChunkCoord coord = north ? new(0, neighbour, level) : new(neighbour, 0, level);
                    WorldMapChunkImage small = WorldMapRasterizer.Rasterize(source, style, coord);
                    for (int row = 0; row < small.Size; row++)
                        for (int column = 0; column < small.Size; column++)
                        {
                            int largeRow = (north ? 1 - neighbour : 1) * small.Size + row;
                            int largeColumn = (north ? 0 : neighbour) * small.Size + column;
                            Assert.Equal(large.Rgba.AsSpan((largeRow * large.Size + largeColumn) * 4, 4).ToArray(),
                                small.Rgba.AsSpan((row * small.Size + column) * 4, 4).ToArray());
                        }
                }
            }
    }

    /// <summary>Cache invalidation includes symbols that extend past their neighbouring authored footprints.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void SizedSymbol_ChangesInvalidateNeighbouringRaster(int level)
    {
        float scale = MathF.ScaleB(1f, level);
        var source = new TestWorldMapSource(new(0f, 0f, 32f * scale, 8f * scale), 0.125f * scale);
        var style = SymbolSizeTests.Style(SymbolSizeTests.Sized(WorldMapSymbolKind.Tree, new(32f * scale)));
        using var cache = new WorldMapChunkCache(source, style);
        var chunk = new WorldMapChunkCoord(0, 0, level);
        cache.Request(chunk, 0f);
        Assert.True(cache.BuildNext());
        Assert.True(cache.IsCurrent(chunk));

        source.RaiseChanged(new(18f * scale, 4f * scale, scale, scale));

        Assert.False(cache.IsCurrent(chunk));
    }

    /// <summary>The largest visible half diagonal includes the fallback and excludes hidden symbols.</summary>
    [Fact]
    public void SizedSymbol_RasterReachIncludesRotatedFallback()
    {
        var style = SymbolSizeTests.Style(SymbolSizeTests.Sized(WorldMapSymbolKind.Building, new(6f, 8f))) with
        {
            FallbackBlocking = SymbolSizeTests.Sized(WorldMapSymbolKind.Obstacle, new(12f, 16f)),
            SymbolRules = [new(new(), SymbolSizeTests.Sized(WorldMapSymbolKind.Building, new(6f, 8f))),
                new(new(), SymbolSizeTests.Sized(WorldMapSymbolKind.Hidden, new(100f)))]
        };
        Assert.Equal(10.25f, WorldMapRasterReach.FootprintMetres(style));
    }

    /// <summary>Raster-only size changes leave icon anchors and their half-open chunk ownership identical.</summary>
    [Fact]
    public void SizedSymbol_FeatureAnchorsAreUnchanged()
    {
        var source = SymbolSizeTests.Source();
        source.Footprints.Add(SymbolSizeTests.Footprint(new(0.5f)) with { Center = new(12f, 4f) });
        var style = SymbolSizeTests.Style(new(WorldMapSymbolKind.Tree, Color.White, Color.Black)) with
        {
            IconRules = [new(WorldMapFeatureSource.Footprint, ["tree"], "tree", 1)]
        };
        var sized = style with { SymbolRules = [new(new(), SymbolSizeTests.Sized(WorldMapSymbolKind.Tree, new(20f)))] };
        foreach (int level in new[] { 0, 1, 5 })
        {
            var west = new WorldMapChunkCoord(0, 0, level);
            WorldMapChunkFeatures original = WorldMapFeatureCollector.Collect(source, style, west);
            WorldMapChunkFeatures updated = WorldMapFeatureCollector.Collect(source, sized, west);
            Assert.Equal(original.Icons, updated.Icons);
            Assert.Equal(original.Labels, updated.Labels);
            Assert.Equal(level == 0 ? 0 : 1, updated.Icons.Count);
            Assert.Equal(new Vector2(12f, 4f), Assert.Single(
                WorldMapFeatureCollector.Collect(source, sized, new(level == 0 ? 1 : 0, 0, level)).Icons).Position);
        }
    }

    /// <summary>Coarse large-world imagery with a sized style still obeys the same retained-image cap.</summary>
    [Fact]
    public void SizedSymbol_LargeWorldRetentionRemainsBounded()
    {
        var source = new TestWorldMapSource(new(0f, 0f, 6400f, 6400f), 64f);
        var style = SymbolSizeTests.Style(SymbolSizeTests.Sized(WorldMapSymbolKind.Tree, new(3.5f)));
        using var cache = new WorldMapChunkCache(source, style, maxRetainedChunks: 2);
        int level = WorldMapChunking.MaxLevel(source.Bounds, style.ChunkMetres);
        foreach (WorldMapChunkCoord chunk in new WorldMapChunkCoord[] { new(0, 0, level), new(0, 0), new(1, 0) })
        {
            cache.Request(chunk, 0f);
            Assert.True(cache.BuildNext());
            Assert.InRange(cache.RetainedCount, 1, 2);
        }
        Assert.Equal(2, cache.RetainedCount);
    }
}
