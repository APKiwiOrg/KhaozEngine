using System;
using System.Numerics;

namespace KhaozEngine.Terrain
{
    /// <summary>Maps a <see cref="ChunkCoord"/> to and from world space for a given chunk size. One source of truth
    /// shared by <see cref="TerrainStreamer"/>, <c>Scene3DChunkSink</c>, and the tests, so the grid math
    /// never drifts. <see cref="AreaOf"/> returns a half-open rect so adjacent chunks tile <see cref="PropScatter"/>
    /// exactly once (streaming-invariant).</summary>
    public static class ChunkGrid
    {
        /// <summary>The chunk containing the world point. Floors toward negative infinity (matches CellCoord), so a
        /// point on a chunk's lower edge belongs to that chunk and negatives floor downward, not toward zero.</summary>
        public static ChunkCoord CoordOf(float worldX, float worldZ, float chunkSize) =>
            new((int)MathF.Floor(worldX / chunkSize), (int)MathF.Floor(worldZ / chunkSize));

        /// <summary>World XZ midpoint of the chunk (used for distance-to-LOD).</summary>
        public static Vector2 CenterOf(ChunkCoord c, float chunkSize) =>
            new((c.X + 0.5f) * chunkSize, (c.Z + 0.5f) * chunkSize);

        /// <summary>The meshing region for the chunk (its -X/-Z corner + size).</summary>
        public static TerrainChunkRegion RegionOf(ChunkCoord c, float chunkSize) =>
            new() { OriginX = c.X * chunkSize, OriginZ = c.Z * chunkSize, Size = chunkSize };

        /// <summary>The half-open [origin, origin+size) prop-scatter window for the chunk.</summary>
        public static RectArea AreaOf(ChunkCoord c, float chunkSize) =>
            new(c.X * chunkSize, c.Z * chunkSize, (c.X + 1) * chunkSize, (c.Z + 1) * chunkSize);
    }
}
