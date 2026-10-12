using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.WorldMap;

/// <summary>An integer chunk address relative to the map-space origin.</summary>
/// <param name="X">The eastward chunk coordinate.</param>
/// <param name="Y">The northward chunk coordinate.</param>
public readonly record struct WorldMapChunkCoord(int X, int Y);

/// <summary>Addresses origin-aligned square chunks in north-up map space.</summary>
public static class WorldMapChunking
{
    /// <summary>Floors both axes to find the chunk containing a map position.</summary>
    /// <param name="mapPosition">The position in metres.</param>
    /// <param name="chunkMetres">A finite, positive chunk edge length.</param>
    public static WorldMapChunkCoord ChunkAt(Vector2 mapPosition, float chunkMetres)
    {
        ValidateChunkSize(chunkMetres);
        return new WorldMapChunkCoord(
            checked((int)Math.Floor((double)mapPosition.X / chunkMetres)),
            checked((int)Math.Floor((double)mapPosition.Y / chunkMetres)));
    }

    /// <summary>Returns a chunk's square bounds with a south-west origin.</summary>
    /// <param name="chunk">The integer chunk address.</param>
    /// <param name="chunkMetres">A finite, positive chunk edge length.</param>
    public static MapRect ChunkRect(WorldMapChunkCoord chunk, float chunkMetres)
    {
        ValidateChunkSize(chunkMetres);
        return new MapRect(chunk.X * chunkMetres, chunk.Y * chunkMetres, chunkMetres, chunkMetres);
    }

    /// <summary>
    /// Enumerates chunks intersecting a half-open area, south row first and west first within each row.
    /// An empty area yields no chunks.
    /// </summary>
    /// <param name="area">The map-space area to cover.</param>
    /// <param name="chunkMetres">A finite, positive chunk edge length.</param>
    public static IEnumerable<WorldMapChunkCoord> ChunksTouching(MapRect area, float chunkMetres)
    {
        ValidateChunkSize(chunkMetres);
        if (area.Width <= 0f || area.Height <= 0f) yield break;

        WorldMapChunkCoord first = ChunkAt(new Vector2(area.X, area.Y), chunkMetres);
        int lastX = checked((int)(Math.Ceiling((double)area.Right / chunkMetres) - 1d));
        int lastY = checked((int)(Math.Ceiling((double)area.Top / chunkMetres) - 1d));
        // Widen the counters so the last representable chunk cannot wrap to the opposite edge.
        for (long y = first.Y; y <= lastY; y++)
            for (long x = first.X; x <= lastX; x++)
                yield return new WorldMapChunkCoord((int)x, (int)y);
    }

    static void ValidateChunkSize(float chunkMetres)
    {
        if (!float.IsFinite(chunkMetres) || chunkMetres <= 0f)
            throw new ArgumentOutOfRangeException(nameof(chunkMetres), "Chunk size must be finite and positive.");
    }
}
