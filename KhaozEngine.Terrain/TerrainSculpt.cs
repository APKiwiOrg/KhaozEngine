using System;
using System.Collections.Generic;

namespace KhaozEngine.Terrain;

/// <summary>Runtime composition of authored height deltas over the analytic base. A sparse map of
/// <see cref="TileSize"/> x <see cref="TileSize"/> delta tiles at a fixed sculpt cell size;
/// <see cref="SampleDelta"/> bilinearly interpolates the authored deltas between cell centers and returns 0
/// outside every stored tile. Read-only and deterministic: the delta at (x, z) depends only on (x, z) and
/// the stored tiles, so composited terrain stays stateless and every head agrees. Attach one to a
/// <see cref="TerrainField"/> to fold hand-sculpted topology into <see cref="TerrainField.SampleHeight"/>.
/// A global cell (cellX, cellZ) has its center at world (cellX * <see cref="CellSize"/>,
/// cellZ * CellSize).</summary>
public sealed class TerrainSculpt
{
    /// <summary>Cells per tile edge. A tile stores <see cref="TileSize"/> squared deltas.</summary>
    public const int TileSize = 32;

    readonly float _cellSize;
    readonly Dictionary<long, float[]> _tiles;

    /// <summary>Builds a sculpt from its cell size and tiles. Each tile's delta array is referenced, not
    /// copied, so treat it as owned by the sculpt afterwards. A later tile at the same coordinate
    /// replaces an earlier one.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cellSize"/> is not positive and finite.</exception>
    public TerrainSculpt(float cellSize, IEnumerable<TerrainSculptTile> tiles)
    {
        if (!(cellSize > 0f) || float.IsInfinity(cellSize))
            throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "sculpt cell size must be positive and finite.");
        ArgumentNullException.ThrowIfNull(tiles);
        _cellSize = cellSize;
        _tiles = new Dictionary<long, float[]>();
        foreach (TerrainSculptTile t in tiles)
            _tiles[Key(t.TileX, t.TileZ)] = t.Deltas;
    }

    /// <summary>The world size of one sculpt cell, in meters.</summary>
    public float CellSize => _cellSize;

    /// <summary>True when no tiles are stored, so <see cref="SampleDelta"/> is uniformly 0.</summary>
    public bool IsEmpty => _tiles.Count == 0;

    /// <summary>Number of stored delta tiles.</summary>
    public int TileCount => _tiles.Count;

    /// <summary>The authored height delta (meters) at a world point, bilinearly interpolated between the
    /// four surrounding cell centers. 0 where no tile covers a corner, so an unsculpted region reads 0.</summary>
    public float SampleDelta(float x, float z)
    {
        if (_tiles.Count == 0) return 0f;
        float gx = x / _cellSize, gz = z / _cellSize;
        int i0 = (int)MathF.Floor(gx), j0 = (int)MathF.Floor(gz);
        float fx = gx - i0, fz = gz - j0;
        float d00 = CellDelta(i0, j0), d10 = CellDelta(i0 + 1, j0);
        float d01 = CellDelta(i0, j0 + 1), d11 = CellDelta(i0 + 1, j0 + 1);
        float top = d00 + (d10 - d00) * fx;
        float bottom = d01 + (d11 - d01) * fx;
        return top + (bottom - top) * fz;
    }

    float CellDelta(int cellX, int cellZ)
    {
        int tileX = FloorDivTile(cellX), tileZ = FloorDivTile(cellZ);
        if (!_tiles.TryGetValue(Key(tileX, tileZ), out float[]? deltas)) return 0f;
        int localX = cellX - tileX * TileSize, localZ = cellZ - tileZ * TileSize;
        return deltas[localZ * TileSize + localX];
    }

    /// <summary>Floor-divide a global cell index by the tile size, correct for negative cells.</summary>
    static int FloorDivTile(int cell) => cell >= 0 ? cell / TileSize : (cell - (TileSize - 1)) / TileSize;

    static long Key(int tileX, int tileZ) => ((long)tileX << 32) | (uint)tileZ;
}
