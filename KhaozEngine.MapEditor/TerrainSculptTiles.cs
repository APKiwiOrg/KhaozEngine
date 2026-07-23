using System.Collections.Generic;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEditor;

/// <summary>Groups a brush dab's per-cell writes (<see cref="TerrainSculptBrush.ComputeDab"/>) into
/// <see cref="TerrainSculpt.TileSize"/>-cell tiles, ready for <see cref="TerrainSculptStrokeCommand"/>. Shared by
/// the interactive sculpt tool (<c>EditorToolController.ApplyDab</c>) and the <c>sculpt_apply</c>/
/// <c>sculpt_flatten_region</c> MCP verbs (T3, #271), so both surfaces build tiles the same way instead of
/// carrying their own copy.</summary>
public static class TerrainSculptTiles
{
    /// <summary>Groups <paramref name="writes"/> into tiles, snapshotting each touched tile's prior grid (null
    /// when the tile does not exist yet in <paramref name="overrides"/>) and building its final grid (prior-or-zero
    /// with the writes applied). Also returns the writes' world footprint, padded one cell for the bilinear reach
    /// of a delta into its neighbours, for the command's dirty region. Returns an empty list (and a zero-area
    /// <paramref name="dabBounds"/>) for an empty <paramref name="writes"/>.</summary>
    public static List<SculptTileDelta> BuildTileDeltas(MapTerrainOverrides? overrides, float cellSize,
        IReadOnlyList<TerrainSculptBrush.CellWrite> writes, out RectArea dabBounds)
    {
        const int span = TerrainSculpt.TileSize;
        var work = new Dictionary<long, (int Tx, int Tz, float[]? Prior, float[] Final)>();
        int minCx = int.MaxValue, minCz = int.MaxValue, maxCx = int.MinValue, maxCz = int.MinValue;

        for (int i = 0; i < writes.Count; i++)
        {
            TerrainSculptBrush.CellWrite w = writes[i];
            if (w.CellX < minCx) minCx = w.CellX;
            if (w.CellX > maxCx) maxCx = w.CellX;
            if (w.CellZ < minCz) minCz = w.CellZ;
            if (w.CellZ > maxCz) maxCz = w.CellZ;

            int tx = FloorDiv(w.CellX, span), tz = FloorDiv(w.CellZ, span);
            long key = ((long)tx << 32) | (uint)tz;
            if (!work.TryGetValue(key, out (int Tx, int Tz, float[]? Prior, float[] Final) entry))
            {
                float[]? prior = null;
                float[] final;
                if (overrides is not null && overrides.TryGetTile(tx, tz, out MapSculptTile existing))
                {
                    prior = (float[])existing.Deltas.Clone();
                    final = (float[])existing.Deltas.Clone();
                }
                else
                {
                    final = new float[span * span];
                }
                entry = (tx, tz, prior, final);
                work[key] = entry;
            }
            int lx = w.CellX - tx * span, lz = w.CellZ - tz * span;
            entry.Final[lz * span + lx] = w.Delta;
        }

        var tiles = new List<SculptTileDelta>(work.Count);
        foreach ((int Tx, int Tz, float[]? Prior, float[] Final) e in work.Values)
            tiles.Add(new SculptTileDelta(e.Tx, e.Tz, e.Prior, e.Final));

        dabBounds = writes.Count == 0
            ? default
            : new RectArea(minCx * cellSize - cellSize, minCz * cellSize - cellSize,
                maxCx * cellSize + cellSize, maxCz * cellSize + cellSize);
        return tiles;
    }

    /// <summary>Floor-divide a global cell index by the tile size, correct for negative cells (matches
    /// <see cref="MapTerrainOverrides"/>).</summary>
    public static int FloorDiv(int cell, int span) => cell >= 0 ? cell / span : (cell - (span - 1)) / span;
}
