using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.WorldMap;
using Xunit;

namespace KhaozEngine.Tests.WorldMap;

/// <summary>Drawn symbol dimensions preserve authored centres, rotations and coarse collection budgets.</summary>
public sealed class SymbolSizeTests
{
    /// <summary>Every visible kind uses the requested local dimensions around the authored centre.</summary>
    [Theory]
    [InlineData(WorldMapSymbolKind.Bridge, 0f)]
    [InlineData(WorldMapSymbolKind.Obstacle, 0f)]
    [InlineData(WorldMapSymbolKind.Wall, 0f)]
    [InlineData(WorldMapSymbolKind.Building, 0f)]
    [InlineData(WorldMapSymbolKind.Tree, 0f)]
    [InlineData(WorldMapSymbolKind.Tree, 1.5707964f)]
    [InlineData(WorldMapSymbolKind.Building, 1.5707964f)]
    public void SizedSymbol_DrawsRequestedDimensionsAtAuthoredPose(WorldMapSymbolKind kind, float rotation)
    {
        var source = Source();
        source.Footprints.Add(Footprint(new(0.5f), rotation));
        WorldMapChunkImage image = WorldMapRasterizer.Rasterize(source, Style(Sized(kind, new(4f, 2f))), new(0, 0));

        Vector2 inside = rotation == 0f ? new(5.375f, 4.125f) : new(4.125f, 5.375f);
        Vector2 outside = rotation == 0f ? new(6.125f, 4.125f) : new(4.125f, 6.125f);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(image, inside));
        Assert.Equal(new byte[] { 128, 128, 128, 255 }, Pixel(image, outside));
    }

    /// <summary>The size can shrink a symbol without changing its authored footprint.</summary>
    [Fact]
    public void SizedSymbol_CanShrinkAnAuthoredFootprint()
    {
        var source = Source();
        source.Footprints.Add(Footprint(new(2f)));
        WorldMapChunkImage image = WorldMapRasterizer.Rasterize(source,
            Style(Sized(WorldMapSymbolKind.Building, new(1f))), new(0, 0));
        Assert.Equal(new byte[] { 128, 128, 128, 255 }, Pixel(image, new(5.375f, 4.125f)));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(image, new(4.125f)));
    }

    /// <summary>Building shadows use the same drawn dimensions as their symbols.</summary>
    [Fact]
    public void SizedBuilding_ShadowUsesDrawnDimensions()
    {
        var source = Source();
        source.Footprints.Add(Footprint(new(0.5f)));
        var style = Style(Sized(WorldMapSymbolKind.Building, new(4f, 2f))) with
        {
            BuildingShadow = Color.Black,
            BuildingShadowOffsetMetres = new(0f, 2f)
        };
        Assert.Equal(new byte[] { 0, 0, 0, 255 },
            Pixel(WorldMapRasterizer.Rasterize(source, style, new(0, 0)), new(5.375f, 6.125f)));
    }

    /// <summary>Eligible trunks show enlarged crowns, while sub-pixel trunks remain filtered before materialization.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CoarseLevel_FiltersByAuthoredSize_ThenDrawsEligibleCrown(bool sourceFilters)
    {
        var source = Source();
        source.ApplyFootprintSizeFilter = sourceFilters;
        var style = Style(Sized(WorldMapSymbolKind.Tree, new(8f)));
        byte[] empty = WorldMapRasterizer.Rasterize(source, style, new(0, 0, 3)).Rgba;
        source.Footprints.Add(Footprint(new(0.25f)));
        Assert.Equal(empty, WorldMapRasterizer.Rasterize(source, style, new(0, 0, 3)).Rgba);
        Assert.Equal(1f, source.LastMinExtentMetres);
        Assert.Equal(sourceFilters ? 0 : 1, source.MaterializedFootprints);

        source.Footprints[0] = Footprint(new(0.5f));
        WorldMapChunkImage admitted = WorldMapRasterizer.Rasterize(source, style, new(0, 0, 3));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(admitted, new(5f), 0.5f));
    }

    /// <summary>Invalid dimensions are rejected before either raster sampling or cache construction.</summary>
    [Theory]
    [InlineData(0f, 2f)]
    [InlineData(2f, -1f)]
    [InlineData(float.NaN, 2f)]
    [InlineData(2f, float.PositiveInfinity)]
    public void SizedSymbol_InvalidDimensionsAreRejected(float width, float depth)
    {
        var source = Source();
        var style = Style(Sized(WorldMapSymbolKind.Tree, new(width, depth)));
        Assert.Throws<ArgumentException>(() => WorldMapRasterizer.Rasterize(source, style, new(0, 0)));
        Assert.Equal(0, source.SampleCalls);
        Assert.Throws<ArgumentException>(() => new WorldMapChunkCache(source, style));
    }

    internal static WorldMapSymbol Sized(WorldMapSymbolKind kind, Vector2 size) =>
        new(kind, Color.White, Color.Black) { SizeMetres = size };

    internal static WorldMapStyle Style(WorldMapSymbol symbol) => new()
    {
        ChunkMetres = 8f,
        PixelsPerMetre = 4f,
        OutlinePixels = 0f,
        BuildingShadow = Color.Transparent,
        BuildingShadowOffsetMetres = Vector2.Zero,
        WallWidthMetres = 0f,
        Water = new WorldMapStyle().Water with { ShorelinePixels = 0 },
        SymbolRules = [new(new(), symbol)]
    };

    internal static WorldMapFootprint Footprint(Vector2 halfExtents, float rotation = 0f) =>
        new(1, "tree", new(4f), halfExtents, rotation, ["tree"], false, WorldMapCollision.Solid);

    internal static TestWorldMapSource Source()
    {
        var source = new TestWorldMapSource(new(-8f, -8f, 144f, 144f), 4f);
        for (int row = 0; row < 36; row++)
            for (int column = 0; column < 36; column++)
                source.SetCell(column, row, Color.FromHex("#808080"), 0f);
        return source;
    }

    internal static byte[] Pixel(WorldMapChunkImage image, Vector2 position, float ppm = 4f)
    {
        int column = (int)(position.X * ppm);
        int row = image.Size - 1 - (int)(position.Y * ppm);
        return image.Rgba.AsSpan((row * image.Size + column) * 4, 4).ToArray();
    }
}
