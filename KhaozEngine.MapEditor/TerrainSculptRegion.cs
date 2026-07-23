using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEditor;

/// <summary>Headless region-scale sculpt operations (T3, #271): unlike <see cref="TerrainSculptBrush"/>'s falloff
/// dab, these touch a whole region uniformly with no falloff and no dt blending, so a region flatten or a region
/// clear lands as one exact, deterministic computation instead of an approximation built from repeated dabs.
/// GPU-free and document-free (reads through delegates / an already-loaded overrides layer), so both operations
/// stay headless-testable the same way <see cref="TerrainSculptBrush"/> is.</summary>
public static class TerrainSculptRegion
{
    /// <summary>Computes the exact per-cell height-delta writes that flatten every sculpt cell whose centre falls
    /// inside the inclusive world rect [<paramref name="minX"/>..<paramref name="maxX"/>] x
    /// [<paramref name="minZ"/>..<paramref name="maxZ"/>] (clamped to <paramref name="bounds"/>) to
    /// <paramref name="targetHeight"/>: delta = targetHeight - analytic base height at the cell centre, so the
    /// composited surface reads exactly <paramref name="targetHeight"/> there regardless of the existing analytic
    /// shape. Reads the current delta field through <paramref name="currentDelta"/> so only cells whose delta
    /// actually changes are returned (an already-flat cell is skipped). Empty when the rect is degenerate
    /// (<paramref name="minX"/> &gt; <paramref name="maxX"/> or <paramref name="minZ"/> &gt; <paramref name="maxZ"/>),
    /// <paramref name="bounds"/> has no area, or the rect misses <paramref name="bounds"/> entirely.</summary>
    public static IReadOnlyList<TerrainSculptBrush.CellWrite> ComputeFlattenRegion(
        float minX, float minZ, float maxX, float maxZ, float targetHeight, float cellSize, in SculptBounds bounds,
        Func<int, int, float> currentDelta, Func<float, float, float> baseHeight)
    {
        ArgumentNullException.ThrowIfNull(currentDelta);
        ArgumentNullException.ThrowIfNull(baseHeight);
        var writes = new List<TerrainSculptBrush.CellWrite>();
        if (!(cellSize > 0f) || !bounds.HasArea || minX > maxX || minZ > maxZ) return writes;

        int cxMin = Math.Max((int)MathF.Ceiling(minX / cellSize), bounds.MinCellX);
        int cxMax = Math.Min((int)MathF.Floor(maxX / cellSize), bounds.MaxCellX);
        int czMin = Math.Max((int)MathF.Ceiling(minZ / cellSize), bounds.MinCellZ);
        int czMax = Math.Min((int)MathF.Floor(maxZ / cellSize), bounds.MaxCellZ);

        for (int cz = czMin; cz <= czMax; cz++)
        {
            for (int cx = cxMin; cx <= cxMax; cx++)
            {
                float wx = cx * cellSize, wz = cz * cellSize;
                float cur = currentDelta(cx, cz);
                float next = targetHeight - baseHeight(wx, wz);
                if (next != cur) writes.Add(new TerrainSculptBrush.CellWrite(cx, cz, next));
            }
        }
        return writes;
    }

    /// <summary>Selects the tiles a clear touches: every stored tile in <paramref name="overrides"/> when
    /// <paramref name="minX"/> is null (a whole-layer clear), otherwise only the tiles whose world extent (their
    /// <see cref="TerrainSculpt.TileSize"/>-cell footprint at <paramref name="overrides"/>'s cell size) intersects
    /// the inclusive rect [<paramref name="minX"/>..<paramref name="maxX"/>] x
    /// [<paramref name="minZ"/>..<paramref name="maxZ"/>]. Callers pass all four rect bounds together or none (a
    /// null <paramref name="minX"/> is the sole "no region" signal). Each selected tile carries a defensive clone
    /// of its current grid, the prior state a clear command restores on undo.</summary>
    public static IReadOnlyList<SculptTileClear> SelectClearTiles(MapTerrainOverrides overrides,
        float? minX, float? minZ, float? maxX, float? maxZ)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        const int span = TerrainSculpt.TileSize;
        float cellSize = overrides.CellSize;
        var selected = new List<SculptTileClear>();

        foreach (MapSculptTile tile in overrides.Tiles)
        {
            if (minX is { } rectMinX)
            {
                float tMinX = tile.TileX * span * cellSize, tMinZ = tile.TileZ * span * cellSize;
                float tMaxX = tMinX + span * cellSize, tMaxZ = tMinZ + span * cellSize;
                if (tMaxX < rectMinX || tMinX > maxX!.Value || tMaxZ < minZ!.Value || tMinZ > maxZ!.Value) continue;
            }
            selected.Add(new SculptTileClear(tile.TileX, tile.TileZ, (float[])tile.Deltas.Clone()));
        }
        return selected;
    }
}
