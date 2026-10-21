using System;
using KhaozEngine.Primitives;
using KhaozEngine.WorldMap;
using Xunit;

namespace KhaozEngine.Tests.WorldMap;

/// <summary>Contour neighbours use the same bounded source reach as cache invalidation.</summary>
public sealed class ContourBoundaryTests
{
    /// <summary>Independently sampled negative chunks match one larger raster at every channel.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Contours_NegativeNeighbouringChunks_MatchLargerRaster(int level)
    {
        TestWorldMapSource source = ContourTests.Surface((x, y) => 0.8f * x + 0.4f * y + 0.3f * MathF.Sin(x));
        WorldMapStyle style = ContourTests.Style(2f, 1.5f);
        WorldMapChunkImage large = WorldMapRasterizer.Rasterize(source, style with { ChunkMetres = 16f }, new(-1, -1, level));
        for (int y = -2; y <= -1; y++)
            for (int x = -2; x <= -1; x++)
            {
                WorldMapChunkImage small = WorldMapRasterizer.Rasterize(source, style, new(x, y, level));
                for (int row = 0; row < 32; row++)
                    for (int column = 0; column < 32; column++)
                        Assert.Equal(ContourTests.Pixel(large, (x + 2) * 32 + column, (-1 - y) * 32 + row),
                            ContourTests.Pixel(small, column, row));
            }
    }

    /// <summary>A wide contour uses a single bounded read including its finite-difference neighbourhood.</summary>
    [Fact]
    public void Contours_WideStroke_ExtendsSamplingReach()
    {
        TestWorldMapSource source = ContourTests.Surface((x, _) => x);
        MapRect? sampled = null;
        source.BeforeSample = area => sampled = area;

        WorldMapRasterizer.Rasterize(source, ContourTests.Style(4f, 16f), new(-1, -1));

        Assert.Equal(1, source.SampleCalls);
        Assert.Equal(new MapRect(-11.25f, -11.25f, 14.5f, 14.5f), sampled);
    }

    /// <summary>A changed source sample inside contour reach invalidates and rebuilds its neighbour.</summary>
    [Fact]
    public void Contours_SourceChanged_UsesContourReachAndPublishesNewRevision()
    {
        TestWorldMapSource source = ContourTests.Surface((x, _) => x);
        using var cache = new WorldMapChunkCache(source, ContourTests.Style(4f, 16f));
        cache.Request(new(0, 0), 0f);
        Assert.True(cache.BuildNext());
        Assert.True(cache.TryGet(new(0, 0), out var original));
        source.SetCell(84, 72, Color.White, 40f);
        source.Revision++;

        source.RaiseChanged(new(10f, 4f, 0.5f, 0.5f));

        Assert.False(cache.IsCurrent(new(0, 0)));
        Assert.True(cache.BuildNext());
        Assert.True(cache.TryGet(new(0, 0), out var rebuilt));
        Assert.True(rebuilt.Generation > original.Generation);
        Assert.Equal(1, rebuilt.Image.Revision);
        Assert.True(cache.IsCurrent(new(0, 0)));
    }

    /// <summary>A notified metre-height edit changes the current contour imagery after rebuilding.</summary>
    [Fact]
    public void Contours_SourceHeightChanged_RebuildsContourPixels()
    {
        TestWorldMapSource source = ContourTests.Surface((x, _) => x);
        using var cache = new WorldMapChunkCache(source, ContourTests.Style(4f, 2f));
        cache.Request(new(0, 0), 0f);
        Assert.True(cache.BuildNext());
        Assert.True(cache.TryGet(new(0, 0), out var original));
        Assert.Equal(0, ContourTests.Red(original.Image, 15, 16));
        source.SetCell(71, 71, Color.White, 8f);
        source.Revision++;

        source.RaiseChanged(new(3.5f, 3.5f, 0.5f, 0.5f));

        Assert.False(cache.IsCurrent(new(0, 0)));
        Assert.True(cache.BuildNext());
        Assert.True(cache.TryGet(new(0, 0), out var rebuilt));
        Assert.NotEqual(original.Image.Rgba, rebuilt.Image.Rgba);
        Assert.True(cache.IsCurrent(new(0, 0)));
    }
}
