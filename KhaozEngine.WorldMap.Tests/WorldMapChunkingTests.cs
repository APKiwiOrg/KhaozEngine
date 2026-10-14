using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.WorldMap;
using Xunit;

namespace KhaozEngine.Tests.WorldMap;

/// <summary>Proves chunk ownership and half-open enumeration in north-up map space.</summary>
public sealed class WorldMapChunkingTests
{
    /// <summary>Negative positions belong to the chunk west or south of the origin.</summary>
    [Fact]
    public void ChunkAt_NegativeCoordinates_Floors()
    {
        Assert.Equal(new WorldMapChunkCoord(-1, 0), WorldMapChunking.ChunkAt(new Vector2(-0.5f, 10f), 64f));
        Assert.Equal(new WorldMapChunkCoord(-2, -1), WorldMapChunking.ChunkAt(new Vector2(-64.01f, -0.01f), 64f));
        Assert.Equal(new WorldMapChunkCoord(1, 0), WorldMapChunking.ChunkAt(new Vector2(64f, 0f), 64f));
    }

    /// <summary>Chunk bounds begin at their south-west corner.</summary>
    [Fact]
    public void ChunkRect_IsSouthWestOrigin() =>
        Assert.Equal(new MapRect(-64f, 128f, 64f, 64f), WorldMapChunking.ChunkRect(new WorldMapChunkCoord(-1, 2), 64f));

    /// <summary>An excluded north or east boundary does not add a chunk.</summary>
    [Fact]
    public void ChunksTouching_EdgeOnBoundary_ExcludesNextChunk()
    {
        var chunks = WorldMapChunking.ChunksTouching(new MapRect(0f, 0f, 64f, 64f), 64f).ToList();
        Assert.Equal(new[] { new WorldMapChunkCoord(0, 0) }, chunks);
    }

    /// <summary>Enumeration advances east within each row, then north.</summary>
    [Fact]
    public void ChunksTouching_SpanningNegativeArea_RowMajorSouthFirst()
    {
        var chunks = WorldMapChunking.ChunksTouching(new MapRect(-10f, -10f, 20f, 20f), 64f).ToList();
        Assert.Equal(new[] { new WorldMapChunkCoord(-1, -1), new(0, -1), new(-1, 0), new(0, 0) }, chunks);
    }

    /// <summary>An area with no width has no chunks.</summary>
    [Fact]
    public void ChunksTouching_ZeroArea_IsEmpty() =>
        Assert.Empty(WorldMapChunking.ChunksTouching(new MapRect(5f, 5f, 0f, 3f), 64f));

    /// <summary>Negative boundary edges and non-default chunk sizes use the same ownership rule.</summary>
    [Fact]
    public void ChunksTouching_NegativeBoundary_ExcludesNorthAndEastNeighbours()
    {
        Assert.Equal(new[] { new WorldMapChunkCoord(-2, -1) },
            WorldMapChunking.ChunksTouching(new MapRect(-32f, -16f, 16f, 16f), 16f).ToArray());
        Assert.Empty(WorldMapChunking.ChunksTouching(new MapRect(5f, 5f, 3f, 0f), 64f));
    }

    /// <summary>Invalid chunk sizes are refused rather than producing meaningless coordinates.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Chunking_InvalidChunkSize_IsRejected(float chunkMetres)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WorldMapChunking.ChunkAt(Vector2.Zero, chunkMetres));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorldMapChunking.ChunkRect(new(0, 0), chunkMetres));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WorldMapChunking.ChunksTouching(new MapRect(0f, 0f, 1f, 1f), chunkMetres).ToArray());
    }
}
