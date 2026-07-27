using System;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc;

/// <summary>The <see cref="IChunkBuildGate"/> a <see cref="MapTileResidency"/> hands a
/// <see cref="TerrainStreamer"/>: a chunk is buildable when NO document tile touching its footprint is
/// occupied-but-not-resident.
/// <para>Two details carry the weight. An ABSENT tile (one the manifest's occupied index does not list) is
/// BUILDABLE, not blocked, because absence is the common case in a sparse 100 km world and gating on it would
/// deadlock the streamer over empty terrain forever. And the footprint is expanded by one sculpt span on its -X
/// and -Z sides before being mapped to document tiles, because a sculpt tile belongs to the document tile
/// containing its ORIGIN corner: ground inside this chunk can carry deltas owned by the neighbour on the low
/// side, so the chunk waits for that neighbour too.</para>
/// <para>This is the per-chunk half of the same inset <see cref="MapResidencyConfig.ValidateAgainst"/> checks at
/// startup. The startup check is the backstop, this is the defence.</para>
/// <para>Once the residency behind this gate is DISPOSED, <see cref="CanBuild"/> turns permissive (every chunk
/// builds) instead of refusing forever: a disposed residency reports nothing resident, so the occupied-but-not-
/// resident test above would otherwise defer every occupied tile for good. The cleaner shutdown path is
/// clearing <c>TerrainStreamer.BuildGate</c> back to null before or alongside disposing the residency, so the
/// gate is never consulted during teardown. This permissive fallback only catches a caller that does not do
/// that.</para></summary>
sealed class MapResidencyGate : IChunkBuildGate
{
    readonly MapTileResidency _residency;
    readonly float _chunkSize;
    readonly float _sculptSpan;

    internal MapResidencyGate(MapTileResidency residency, float chunkSize, float sculptCellSize)
    {
        if (!(chunkSize > 0f) || float.IsInfinity(chunkSize))
            throw new ArgumentOutOfRangeException(nameof(chunkSize), chunkSize, "chunk size must be positive and finite.");
        if (!(sculptCellSize > 0f) || float.IsInfinity(sculptCellSize))
            throw new ArgumentOutOfRangeException(nameof(sculptCellSize), sculptCellSize, "sculpt cell size must be positive and finite.");
        _residency = residency;
        _chunkSize = chunkSize;
        _sculptSpan = TerrainSculpt.TileSize * sculptCellSize;
    }

    public bool CanBuild(ChunkCoord coord)
    {
        if (_residency.IsDisposed) return true;

        RectArea area = ChunkGrid.AreaOf(coord, _chunkSize);
        float tileSize = _residency.TileSize;

        // Expand on the low side only: a sculpt tile whose origin sits up to one span to the -X / -Z of this
        // chunk still reaches into it. The high side needs no expansion, since a sculpt tile starting inside the
        // chunk is owned by a document tile the chunk already touches.
        MapTileCoord min = MapTileGrid.CoordOf(area.MinX - _sculptSpan, area.MinZ - _sculptSpan, tileSize);
        MapTileCoord max = new(LastTile(area.MaxX, tileSize), LastTile(area.MaxZ, tileSize));

        for (int z = min.Z; z <= max.Z; z++)
        for (int x = min.X; x <= max.X; x++)
        {
            var tile = new MapTileCoord(x, z);
            if (_residency.Source.Tiles.IsOccupied(tile) && !_residency.IsResident(tile)) return false;
        }
        return true;
    }

    /// <summary>The last tile a HALF-OPEN world span reaches. A chunk's max edge is exclusive, so an edge landing
    /// exactly on a tile boundary must NOT drag the next tile in: waiting on a tile the chunk does not actually
    /// cover is a defer that only clears if that tile happens to be in the residency ring.</summary>
    static int LastTile(float maxEdge, float tileSize)
    {
        int t = (int)MathF.Floor(maxEdge / tileSize);
        return t * tileSize >= maxEdge ? t - 1 : t;
    }
}
