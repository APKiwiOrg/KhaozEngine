using System;
using KhaozEngine.Primitives;
using KhaozEngine.WorldMap;
using KhaozEngine.WorldMap.Raster;
using Xunit;

namespace KhaozEngine.Tests.WorldMap;

/// <summary>Rounded raster dimensions must still fill the map rectangle displayed by full-chunk UVs.</summary>
public sealed class RasterScaleTests
{
    /// <summary>An analytic gradient samples the map position displayed at every texture pixel centre.</summary>
    [Theory]
    [InlineData(1.5f, 4)]
    [InlineData(1.9f, 6)]
    public void RoundedScale_GradientMatchesFullChunkUv(float requestedScale, int pixels)
    {
        var source = Gradient();
        var style = new WorldMapStyle { ChunkMetres = 3f, PixelsPerMetre = requestedScale };
        for (int chunkX = 0; chunkX < 2; chunkX++)
        {
            WorldMapChunkImage image = WorldMapRasterizer.Rasterize(source, style, new(chunkX, 0));
            Assert.Equal(pixels, image.Size);
            for (int row = 0; row < pixels; row++)
                for (int column = 0; column < pixels; column++)
                {
                    float displayedX = chunkX * 3f + (column + 0.5f) * 3f / pixels;
                    int expectedRed = (int)MathF.Round(displayedX / 6f * 255f);
                    Assert.InRange((int)image.Rgba[(row * pixels + column) * 4], expectedRed - 1, expectedRed + 1);
                }
        }
    }

    /// <summary>Adjacent quantized chunks share the pixel lattice of a larger raster at the effective scale.</summary>
    [Theory]
    [InlineData(1.5f, 4)]
    [InlineData(1.9f, 6)]
    public void RoundedScale_AdjacentChunksMatchDoubleSizeRaster(float requestedScale, int pixels)
    {
        var source = Gradient();
        var style = new WorldMapStyle { ChunkMetres = 3f, PixelsPerMetre = requestedScale };
        WorldMapChunkImage large = WorldMapRasterizer.Rasterize(source,
            style with { ChunkMetres = 6f, PixelsPerMetre = pixels / 3f }, new(0, 0));
        Assert.Equal(pixels * 2, large.Size);
        for (int chunkX = 0; chunkX < 2; chunkX++)
        {
            WorldMapChunkImage small = WorldMapRasterizer.Rasterize(source, style, new(chunkX, 0));
            for (int row = 0; row < pixels; row++)
                for (int column = 0; column < pixels; column++)
                {
                    int largeIndex = ((pixels + row) * large.Size + chunkX * pixels + column) * 4;
                    int smallIndex = (row * pixels + column) * 4;
                    Assert.Equal(large.Rgba.AsSpan(largeIndex, 4).ToArray(), small.Rgba.AsSpan(smallIndex, 4).ToArray());
                }
        }
    }

    /// <summary>Sampling and invalidation margins cover pixels at the quantized scale.</summary>
    [Theory]
    [InlineData(1.5f, 0.75f)]
    [InlineData(1.9f, 0.5f)]
    public void RoundedScale_RasterReachUsesActualPixelWidth(float requestedScale, float pixelMetres)
    {
        var style = new WorldMapStyle
        {
            ChunkMetres = 3f,
            PixelsPerMetre = requestedScale,
            BuildingShadowOffsetMetres = new(0f, 0f),
            WallWidthMetres = 0f,
            Water = new WorldMapStyle().Water with { ShorelinePixels = 3 }
        };
        Assert.Equal(pixelMetres, WorldMapRasterReach.FootprintMetres(style));
        Assert.Equal(0.25f + 3f * pixelMetres, WorldMapRasterReach.SurfaceMetres(style, 0.125f));
    }

    static TestWorldMapSource Gradient()
    {
        var source = new TestWorldMapSource(new(-1f, -1f, 8f, 8f), 0.125f);
        for (int row = 0; row < 64; row++)
            for (int column = 0; column < 64; column++)
            {
                float x = -1f + (column + 0.5f) * 0.125f;
                source.SetCell(column, row, new Color(Math.Clamp(x / 6f, 0f, 1f), 0f, 0f), 0f);
            }
        return source;
    }
}
