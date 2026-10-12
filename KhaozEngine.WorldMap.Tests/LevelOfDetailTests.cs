using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.WorldMap;
using KhaozEngine.WorldMap.Raster;
using Xunit;

namespace KhaozEngine.Tests.WorldMap;

/// <summary>Proves pyramid addressing, resolution hints, coarse imagery and per-level ownership.</summary>
public sealed class LevelOfDetailTests
{
    /// <summary>Level two covers four native chunk lengths on each axis.</summary>
    [Fact]
    public void LevelRect_DoublesPerLevel_AlignedToLevelSize()
    {
        Assert.Equal(256f, WorldMapChunking.LevelMetres(64f, 2));
        Assert.Equal(new MapRect(256f, -256f, 256f, 256f), WorldMapChunking.ChunkRect(new(1, -1, 2), 64f));
    }

    /// <summary>Negative positions floor at the selected level rather than truncating toward zero.</summary>
    [Fact]
    public void ChunkAt_LevelAware_FloorsNegative() =>
        Assert.Equal(new WorldMapChunkCoord(-1, 1, 2), WorldMapChunking.ChunkAt(new(-1f, 300f), 64f, 2));

    /// <summary>The smallest level spanning the larger side is selected, including exact and empty bounds.</summary>
    [Fact]
    public void MaxLevel_CoversLargestSide()
    {
        Assert.Equal(7, WorldMapChunking.MaxLevel(new(0f, 0f, 6400f, 3000f), 64f));
        Assert.Equal(6, WorldMapChunking.MaxLevel(new(-200f, 0f, 4096f, 100f), 64f));
        Assert.Equal(0, WorldMapChunking.MaxLevel(new(0f, 0f, 64f, 64f), 64f));
        Assert.Equal(0, WorldMapChunking.MaxLevel(new(0f, 0f, 0f, 6400f), 64f));
    }

    /// <summary>Fractional canonical edges agree across addressing, rectangles and coverage at each level.</summary>
    [Fact]
    public void ChunksTouching_LevelAware_UsesCanonicalHalfOpenEdges()
    {
        var chunk = new WorldMapChunkCoord(-7, 3, 2);
        MapRect rect = WorldMapChunking.ChunkRect(chunk, 0.1f);
        Assert.Equal(chunk, WorldMapChunking.ChunkAt(new(rect.X, rect.Y), 0.1f, 2));
        Assert.Equal(new WorldMapChunkCoord(-6, 3, 2), WorldMapChunking.ChunkAt(new(rect.Right, rect.Y), 0.1f, 2));
        Assert.Equal(new[] { chunk }, WorldMapChunking.ChunksTouching(rect, 0.1f, 2).ToArray());
    }

    /// <summary>Negative levels and unrepresentable scaled chunk lengths are rejected.</summary>
    [Fact]
    public void LevelMetres_InvalidLevel_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WorldMapChunking.LevelMetres(64f, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorldMapChunking.ChunkAt(Vector2.Zero, 64f, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorldMapChunking.ChunkRect(new(0, 0, -1), 64f));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorldMapChunking.LevelMetres(64f, 128));
    }

    /// <summary>Each pyramid step doubles the source's cell-size budget without changing image dimensions.</summary>
    [Fact]
    public void Rasterize_Level_RequestsCoarserCells()
    {
        var source = new TestWorldMapSource(new(-8f, -8f, 528f, 528f));
        var style = new WorldMapStyle();
        Assert.Equal(256, WorldMapRasterizer.Rasterize(source, style, new(0, 0)).Size);
        Assert.Equal(0.5f, source.LastMaxCellSize);
        Assert.Equal(256, WorldMapRasterizer.Rasterize(source, style, new(0, 0, 3)).Size);
        Assert.Equal(4f, source.LastMaxCellSize);
    }

    /// <summary>Sampling reaches use the coarse cell size and convert shoreline and footprint pixels at that level.</summary>
    [Fact]
    public void Rasterize_Level_UsesCoarseSamplingReach()
    {
        var source = new TestWorldMapSource(new(-64f, -64f, 1152f, 1152f), 4f);
        MapRect sampled = default;
        source.BeforeSample = area => sampled = area;
        var style = new WorldMapStyle();
        WorldMapRasterizer.Rasterize(source, style, new(0, 0, 3));
        Assert.Equal(new MapRect(-10f, -10f, 532f, 532f), sampled);
        Assert.Equal(10f, WorldMapRasterReach.SurfaceMetres(style, 4f, 3));
        Assert.Equal(style.BuildingShadowOffsetMetres.Length() + 0.2f + 2f,
            WorldMapRasterReach.FootprintMetres(style, 3));
    }

    /// <summary>Flat ground preserves its authored bytes at native and coarse levels.</summary>
    [Fact]
    public void Rasterize_UniformWorld_EveryLevelIsUniform()
    {
        var source = UniformSource();
        foreach (int level in new[] { 0, 1, 4 })
        {
            WorldMapChunkImage image = WorldMapRasterizer.Rasterize(source, new(), new(0, 0, level));
            Assert.Equal(256, image.Size);
            for (int i = 0; i < image.Rgba.Length; i += 4)
                Assert.Equal(new byte[] { 128, 128, 128, 255 }, image.Rgba.AsSpan(i, 4).ToArray());
        }
    }

    /// <summary>Small rocks disappear below half a raster pixel, and skipped buildings leave no shadows.</summary>
    [Fact]
    public void Rasterize_SubPixelFootprint_SkippedAtCoarseLevel()
    {
        var source = UniformSource();
        var style = new WorldMapStyle
        {
            SymbolRules = [new(new(ArchetypeKey: "building"),
                new(WorldMapSymbolKind.Building, Color.Black, Color.Black))]
        };
        byte[] native = WorldMapRasterizer.Rasterize(source, style, new(0, 0)).Rgba;
        byte[] coarse = WorldMapRasterizer.Rasterize(source, style, new(0, 0, 4)).Rgba;
        source.Footprints.Add(new(1, "rock", new(4f, 4f), new(0.25f), 0f, [], false, WorldMapCollision.Solid));
        source.Footprints.Add(new(2, "building", new(8f, 8f), new(0.25f), 0f, [], true, WorldMapCollision.Solid));
        Assert.False(native.SequenceEqual(WorldMapRasterizer.Rasterize(source, style, new(0, 0)).Rgba));
        Assert.Equal(coarse, WorldMapRasterizer.Rasterize(source, style, new(0, 0, 4)).Rgba);
    }

    /// <summary>Markers and straddling footprint centres have one owner independently at each level.</summary>
    [Fact]
    public void Features_OwnedOncePerLevel()
    {
        var source = new TestWorldMapSource(new(0f, 0f, 256f, 256f), 8f);
        source.Markers.Add(new("edge", new(128f, 32f), ["map"]));
        source.Footprints.Add(new(1, "rock", new(128f, 32f), new(2f), 0f, ["map"], false, WorldMapCollision.Solid));
        var style = new WorldMapStyle
        {
            IconRules = [new(WorldMapFeatureSource.Marker, ["map"], "marker", 1),
                new(WorldMapFeatureSource.Footprint, ["map"], "rock", 1)]
        };
        foreach (int level in new[] { 0, 1 })
        {
            var features = WorldMapChunking.ChunksTouching(source.Bounds, 64f, level)
                .Select(chunk => (Chunk: chunk, Features: WorldMapFeatureCollector.Collect(source, style, chunk)))
                .Where(item => item.Features.Icons.Count > 0).ToArray();
            var owner = Assert.Single(features);
            Assert.Equal(new WorldMapChunkCoord(level == 0 ? 2 : 1, 0, level), owner.Chunk);
            Assert.Equal(2, owner.Features.Icons.Count);
        }
    }

    /// <summary>A local edit invalidates both retained levels while leaving a distant native neighbour current.</summary>
    [Fact]
    public void Invalidate_BumpsTouchedChunksOnEveryRetainedLevel()
    {
        using var cache = new WorldMapChunkCache(new TestWorldMapSource(new(0f, 0f, 256f, 128f)), new());
        foreach (WorldMapChunkCoord chunk in new WorldMapChunkCoord[] { new(0, 0), new(0, 0, 1), new(1, 0) })
        {
            cache.Request(chunk, 0f);
            Assert.True(cache.BuildNext());
        }
        cache.Invalidate(new(31f, 31f, 1f, 1f));
        Assert.False(cache.IsCurrent(new(0, 0)));
        Assert.False(cache.IsCurrent(new(0, 0, 1)));
        Assert.True(cache.IsCurrent(new(1, 0)));
    }

    static TestWorldMapSource UniformSource()
    {
        var source = new TestWorldMapSource(new(-64f, -64f, 1152f, 1152f), 64f);
        for (int row = 0; row < 18; row++)
            for (int column = 0; column < 18; column++)
                source.SetCell(column, row, Color.FromHex("#808080"), 0f);
        return source;
    }
}
