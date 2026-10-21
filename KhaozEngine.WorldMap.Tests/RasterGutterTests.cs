using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.WorldMap;
using KhaozEngine.WorldMap.Raster;
using Xunit;

namespace KhaozEngine.Tests.WorldMap;

/// <summary>A chunk's gutter holds the neighbouring chunks' edge pixels without disturbing its interior.</summary>
public sealed class RasterGutterTests
{
    const int Pixels = 32;

    static readonly WorldMapStyle Style = new()
    {
        ChunkMetres = 8f,
        PixelsPerMetre = 4f,
        Contours = new(1f, Color.FromHex("#3a2a18"), 1.5f),
        SymbolRules = Enum.GetValues<WorldMapSymbolKind>()
            .Select(kind => new WorldMapSymbolRule(new(ArchetypeKey: kind.ToString()),
                new(kind, Color.FromHex("#e05030"), Color.FromHex("#202080")))).ToArray()
    };

    /// <summary>Fractional chunk origins must not round a gutter sample across a source cell boundary.</summary>
    [Fact]
    public void FractionalGutter_MatchesNeighbourAtVoidBoundary()
    {
        var source = new TestWorldMapSource(new(-32f, -4f, 16f, 8f), 0.25f);
        for (int row = 0; row < 32; row++)
            for (int column = 0; column < 31; column++)
                source.SetCell(column, row, Color.White, 0f);
        var style = new WorldMapStyle { ChunkMetres = 2.7f, PixelsPerMetre = 10f };
        WorldMapChunkImage west = WorldMapRasterizer.Rasterize(source, style, new(-10, 0), border: 1);
        WorldMapChunkImage east = WorldMapRasterizer.Rasterize(source, style, new(-9, 0));

        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(east, 0, 0));
        Assert.Equal(Pixel(east, 0, 0), Pixel(west, west.Size, 0));
        foreach (WorldMapChunkCoord coord in new[] { new WorldMapChunkCoord(-10, 0), new(-9, 0) })
        {
            WorldMapChunkImage plain = WorldMapRasterizer.Rasterize(source, style, coord);
            WorldMapChunkImage bordered = WorldMapRasterizer.Rasterize(source, style, coord, border: 1);
            for (int row = 0; row < plain.Size; row++)
                Assert.True(plain.Rgba.AsSpan(plain.ByteOffset(0, row), plain.Size * 4)
                    .SequenceEqual(bordered.Rgba.AsSpan(bordered.ByteOffset(0, row), plain.Size * 4)), $"Row {row} changed.");
        }
    }

    /// <summary>Every gutter pixel equals the matching edge pixel of the independently rasterized neighbour.</summary>
    [Theory]
    [InlineData(0, 0, 0, 8f, 1)]
    [InlineData(-1, 0, 0, 8f, 1)]
    [InlineData(0, 0, 1, 8f, 1)]
    [InlineData(-2, -2, 1, 8f, 1)]
    [InlineData(-10, 0, 0, 2.7f, 1)]
    [InlineData(-10, -10, 0, 2.7f, 1)]
    [InlineData(2, 2, 0, 2.7f, 1)]
    [InlineData(-5, -3, 1, 2.7f, 1)]
    [InlineData(2, -3, 2, 2.7f, 1)]
    [InlineData(2, 2, 0, 2.7f, 30)]
    public void Gutter_MatchesAdjacentChunkEdges(int x, int y, int level, float chunkMetres, int border)
    {
        WorldMapStyle style = Style with { ChunkMetres = chunkMetres, PixelsPerMetre = chunkMetres == 8f ? 4f : 10f };
        int pixels = style.ChunkPixels;
        TestWorldMapSource source = Adversarial();
        AddEdgeFootprints(source, style, new(x, y, level));
        WorldMapChunkImage bordered = WorldMapRasterizer.Rasterize(source, style, new(x, y, level), border);

        Assert.Equal(1, source.SampleCalls);
        Assert.Equal(border, bordered.Border);
        Assert.Equal(pixels, bordered.Size);
        Assert.Equal(pixels + 2 * border, bordered.TextureSize);
        Assert.Equal(bordered.TextureSize * bordered.TextureSize * 4, bordered.Rgba.Length);
        var neighbours = new Dictionary<(int East, int South), WorldMapChunkImage>();
        for (int row = -border; row < pixels + border; row++)
            for (int column = -border; column < pixels + border; column++)
            {
                int east = (int)MathF.Floor((float)column / pixels);
                int south = (int)MathF.Floor((float)row / pixels);
                if (east == 0 && south == 0) continue;
                if (!neighbours.TryGetValue((east, south), out WorldMapChunkImage? neighbour))
                    neighbours[(east, south)] = neighbour =
                        WorldMapRasterizer.Rasterize(source, style, new(x + east, y - south, level));
                Assert.True(Pixel(neighbour, column - east * pixels, row - south * pixels)
                        .SequenceEqual(Pixel(bordered, column, row)),
                    $"Gutter pixel ({column}, {row}) of chunk ({x}, {y}, {level}) differs from its neighbour.");
            }
    }

    /// <summary>The gutter adds pixels around the chunk without changing a single interior byte.</summary>
    [Theory]
    [InlineData(0, 0, 0, 8f)]
    [InlineData(0, 0, 1, 8f)]
    [InlineData(-10, 0, 0, 2.7f)]
    [InlineData(-10, -10, 0, 2.7f)]
    [InlineData(2, 2, 0, 2.7f)]
    [InlineData(-5, -3, 1, 2.7f)]
    [InlineData(2, -3, 2, 2.7f)]
    public void BorderedInterior_IsByteIdenticalToTheBorderlessRaster(int x, int y, int level, float chunkMetres)
    {
        WorldMapStyle style = Style with { ChunkMetres = chunkMetres, PixelsPerMetre = chunkMetres == 8f ? 4f : 10f };
        TestWorldMapSource source = Adversarial();
        AddEdgeFootprints(source, style, new(x, y, level));
        WorldMapChunkImage plain = WorldMapRasterizer.Rasterize(source, style, new(x, y, level));
        WorldMapChunkImage bordered = WorldMapRasterizer.Rasterize(source, style, new(x, y, level), border: 1);

        Assert.Equal(plain.Revision, bordered.Revision);
        for (int row = 0; row < plain.Size; row++)
            Assert.True(plain.Rgba.AsSpan(plain.ByteOffset(0, row), plain.Size * 4)
                .SequenceEqual(bordered.Rgba.AsSpan(bordered.ByteOffset(0, row), plain.Size * 4)), $"Row {row} changed.");
    }

    /// <summary>The borderless overload, explicit zero border and positional constructor share one layout.</summary>
    [Fact]
    public void ZeroBorder_PreservesTheBorderlessLayout()
    {
        TestWorldMapSource source = Adversarial();
        WorldMapChunkImage plain = WorldMapRasterizer.Rasterize(source, Style, new(0, 0));
        WorldMapChunkImage zero = WorldMapRasterizer.Rasterize(source, Style, new(0, 0), border: 0);
        var constructed = new WorldMapChunkImage(new(0, 0), 2, new byte[16], 0);

        Assert.Equal(plain.Rgba, zero.Rgba);
        Assert.Equal(0, plain.Border);
        Assert.Equal(Pixels, plain.TextureSize);
        Assert.Equal(0, constructed.Border);
        Assert.Equal(2, constructed.TextureSize);
        Assert.Equal(12, constructed.ByteOffset(1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => constructed.ByteOffset(2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => constructed with { Border = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => WorldMapRasterizer.Rasterize(source, Style, new(0, 0), border: -1));
    }

    /// <summary>Gutter pixels keep interior-relative coordinates and clip only beyond the gutter.</summary>
    [Fact]
    public void Canvas_Gutter_UsesInteriorRelativeCoordinates()
    {
        var canvas = new RasterCanvas(2, 1, new MapRect(0f, 0f, 2f, 2f), 1f);
        Assert.Equal(new MapRect(-1f, -1f, 4f, 4f), canvas.PaintedRect);
        Assert.Equal(new Vector2(-0.5f, 2.5f), canvas.MapPoint(-1f, -1f));
        canvas.Set(-1, -1, Color.White);
        canvas.Blend(2, 2, Color.White, 1f);
        canvas.Blend(-2, 0, Color.White, 1f);
        canvas.Blend(0, 3, Color.White, 1f);

        byte[] bytes = canvas.ToBytes();

        Assert.Equal(4 * 4 * 4, bytes.Length);
        for (int pixel = 0; pixel < 16; pixel++)
            Assert.Equal(pixel is 0 or 15 ? 255 : 0, bytes[pixel * 4]);
    }

    /// <summary>Cache images carry a one-pixel gutter for filtered display.</summary>
    [Fact]
    public void CacheImages_CarryOnePixelGutter()
    {
        TestWorldMapSource source = Adversarial();
        using var cache = new WorldMapChunkCache(source, Style);
        cache.Request(new(0, 0), 0f);
        Assert.True(cache.BuildNext());
        Assert.True(cache.TryGet(new(0, 0), out WorldMapChunk? built));

        Assert.Equal(1, built.Image.Border);
        Assert.Equal(WorldMapRasterizer.Rasterize(source, Style, new(0, 0), border: 1).Rgba, built.Image.Rgba);
    }

    /// <summary>A change that reaches only a chunk's gutter invalidates that chunk.</summary>
    [Fact]
    public void Invalidate_ReachesTheGutter()
    {
        TestWorldMapSource source = Adversarial();
        using var cache = new WorldMapChunkCache(source, Style);
        cache.Request(new(0, 0), 0f);
        Assert.True(cache.BuildNext());
        float cellSize = WorldMapRasterReach.SampleCellSize(source.SurfaceCellSize, 2f / Style.EffectivePixelsPerMetre);
        float interiorReach = WorldMapRasterReach.SurfaceMetres(Style, cellSize, 0) + WorldMapRasterReach.FootprintMetres(Style, 0);

        // The change lies a tenth of a metre beyond the interior reach and inside the quarter-metre gutter pixel.
        cache.Invalidate(new MapRect(8f + interiorReach + 0.1f, 4f, 0.01f, 0.01f));

        Assert.False(cache.IsCurrent(new(0, 0)));
    }

    // Unrelated noise per cell, sloped relief, wet hollows and symbols across every edge and corner.
    static TestWorldMapSource Adversarial()
    {
        var source = new TestWorldMapSource(new(-32f, -32f, 96f, 96f), 0.5f);
        for (int row = 0; row < 192; row++)
            for (int column = 0; column < 192; column++)
            {
                float x = -32f + (column + 0.5f) * 0.5f;
                float y = -32f + (row + 0.5f) * 0.5f;
                float height = 0.3f * x + 0.2f * y + 1.5f * MathF.Sin(0.7f * x) + MathF.Cos(0.9f * y);
                uint hash = (uint)(column * 73856093 ^ row * 19349663);
                var ground = new Color((hash & 255) / 255f, (hash >> 8 & 255) / 255f, (hash >> 16 & 255) / 255f);
                source.SetCell(column, row, ground, height, height < 0.5f ? 0.5f : float.NaN);
            }
        int id = 0;
        foreach (float edge in new[] { -16f, -8f, 0f, 8f, 16f })
        {
            source.Footprints.Add(Footprint(++id, WorldMapSymbolKind.Building, new(edge, 3.3f), new(1.3f, 0.7f), 0.3f));
            source.Footprints.Add(Footprint(++id, WorldMapSymbolKind.Wall, new(4.6f, edge), new(2f, 0.2f), 0f));
            source.Footprints.Add(Footprint(++id, WorldMapSymbolKind.Tree, new(edge, edge), new(0.9f, 0.9f), 0f));
        }
        return source;
    }

    static WorldMapFootprint Footprint(int id, WorldMapSymbolKind kind, Vector2 center, Vector2 halfExtents, float rotation) =>
        new(id, kind.ToString(), center, halfExtents, rotation, [], kind == WorldMapSymbolKind.Building, WorldMapCollision.Solid);

    static void AddEdgeFootprints(TestWorldMapSource source, WorldMapStyle style, WorldMapChunkCoord coord)
    {
        MapRect rect = WorldMapChunking.ChunkRect(coord, style.ChunkMetres);
        foreach (float x in new[] { rect.X, rect.Right })
            foreach (float y in new[] { rect.Y, rect.Top })
                source.Footprints.Add(Footprint(source.Footprints.Count + 1, WorldMapSymbolKind.Building,
                    new(x, y), new(0.3f, 0.5f), 0.3f));
    }

    static byte[] Pixel(WorldMapChunkImage image, int column, int row) =>
        image.Rgba.AsSpan(image.ByteOffset(column, row), 4).ToArray();
}
