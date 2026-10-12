using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.WorldMap;

/// <summary>An integer chunk address relative to the map-space origin.</summary>
/// <param name="X">The eastward chunk coordinate.</param>
/// <param name="Y">The northward chunk coordinate.</param>
/// <param name="Level">The non-negative pyramid level. Zero is the native chunk size.</param>
public readonly record struct WorldMapChunkCoord(int X, int Y, int Level = 0);

/// <summary>Addresses origin-aligned square chunks in north-up map space.</summary>
public static class WorldMapChunking
{
    /// <summary>Returns the chunk edge length at a pyramid level, doubling for each step above zero.</summary>
    /// <param name="chunkMetres">A finite, positive native chunk edge length.</param>
    /// <param name="level">The non-negative pyramid level.</param>
    /// <exception cref="ArgumentOutOfRangeException">The size or level cannot produce a finite positive length.</exception>
    public static float LevelMetres(float chunkMetres, int level)
    {
        ValidateChunkSize(chunkMetres);
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        float size = MathF.ScaleB(chunkMetres, level);
        if (!float.IsFinite(size))
            throw new ArgumentOutOfRangeException(nameof(level), "Level chunk size must be finite.");
        return size;
    }

    /// <summary>Finds the chunk owning a map position between its canonical half-open edges.</summary>
    /// <param name="mapPosition">The position in metres.</param>
    /// <param name="chunkMetres">A finite, positive chunk edge length.</param>
    /// <param name="level">The non-negative pyramid level, defaulting to zero.</param>
    public static WorldMapChunkCoord ChunkAt(Vector2 mapPosition, float chunkMetres, int level = 0)
    {
        chunkMetres = LevelMetres(chunkMetres, level);
        return new WorldMapChunkCoord(
            CoordinateAt(mapPosition.X, chunkMetres),
            CoordinateAt(mapPosition.Y, chunkMetres), level);
    }

    /// <summary>Returns a chunk's square bounds with a south-west origin.</summary>
    /// <param name="chunk">The integer chunk address.</param>
    /// <param name="chunkMetres">A finite, positive chunk edge length.</param>
    public static MapRect ChunkRect(WorldMapChunkCoord chunk, float chunkMetres)
    {
        chunkMetres = LevelMetres(chunkMetres, chunk.Level);
        float x = Edge(chunk.X, chunkMetres);
        float y = Edge(chunk.Y, chunkMetres);
        return new MapRect(x, y, Edge(checked(chunk.X + 1), chunkMetres) - x,
            Edge(checked(chunk.Y + 1), chunkMetres) - y);
    }

    /// <summary>
    /// Enumerates chunks intersecting a half-open area, south row first and west first within each row.
    /// An empty area yields no chunks.
    /// </summary>
    /// <param name="area">The map-space area to cover.</param>
    /// <param name="chunkMetres">A finite, positive chunk edge length.</param>
    /// <param name="level">The non-negative pyramid level, defaulting to zero.</param>
    public static IEnumerable<WorldMapChunkCoord> ChunksTouching(MapRect area, float chunkMetres, int level = 0)
    {
        _ = LevelMetres(chunkMetres, level);
        if (area.Width <= 0f || area.Height <= 0f) yield break;

        WorldMapChunkCoord first = ChunkAt(new Vector2(area.X, area.Y), chunkMetres, level);
        WorldMapChunkCoord last = ChunkAt(new Vector2(MathF.BitDecrement(area.Right), MathF.BitDecrement(area.Top)), chunkMetres, level);
        // Widen the counters so the last representable chunk cannot wrap to the opposite edge.
        for (long y = first.Y; y <= last.Y; y++)
            for (long x = first.X; x <= last.X; x++)
                yield return new WorldMapChunkCoord((int)x, (int)y, level);
    }

    /// <summary>Returns the smallest pyramid level whose chunk size covers the largest bounds extent.</summary>
    /// <param name="bounds">The world bounds. Empty bounds return zero.</param>
    /// <param name="chunkMetres">A finite, positive native chunk edge length.</param>
    public static int MaxLevel(MapRect bounds, float chunkMetres)
    {
        ValidateChunkSize(chunkMetres);
        if (bounds.Width <= 0f || bounds.Height <= 0f) return 0;
        float extent = MathF.Max(bounds.Width, bounds.Height);
        if (!float.IsFinite(extent)) throw new ArgumentOutOfRangeException(nameof(bounds));
        int level = 0;
        while (LevelMetres(chunkMetres, level) < extent) level++;
        return level;
    }

    internal static float Edge(int index, float chunkMetres) => (float)(index * (double)chunkMetres);

    static int CoordinateAt(float position, float chunkMetres)
    {
        int index = checked((int)Math.Floor((double)position / chunkMetres));
        // Rounded edges can move the quotient to either side of the canonical owner.
        if (position < Edge(index, chunkMetres)) return checked(index - 1);
        if (position >= Edge(checked(index + 1), chunkMetres)) return checked(index + 1);
        return index;
    }

    static void ValidateChunkSize(float chunkMetres)
    {
        if (!float.IsFinite(chunkMetres) || chunkMetres <= 0f)
            throw new ArgumentOutOfRangeException(nameof(chunkMetres), "Chunk size must be finite and positive.");
    }
}
