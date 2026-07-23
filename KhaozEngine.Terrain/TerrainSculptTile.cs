using System;

namespace KhaozEngine.Terrain;

/// <summary>One block of authored height deltas at a tile coordinate, the unit a <see cref="TerrainSculpt"/>
/// is built from. A tile at (<see cref="TileX"/>, <see cref="TileZ"/>) covers global cells
/// [TileX * TileSize .. TileX * TileSize + TileSize - 1] on each axis, where TileSize is
/// <see cref="TerrainSculpt.TileSize"/>. Deltas are meters, row-major (index = localZ * TileSize + localX),
/// added to the analytic height at each cell's world position.</summary>
public readonly struct TerrainSculptTile
{
    /// <summary>Tile X index.</summary>
    public int TileX { get; }

    /// <summary>Tile Z index.</summary>
    public int TileZ { get; }

    /// <summary>The row-major delta grid, length <see cref="TerrainSculpt.TileSize"/> squared, in meters.</summary>
    public float[] Deltas { get; }

    /// <summary>Wraps an existing delta array without copying (so do not mutate it afterwards). The array
    /// length must be <see cref="TerrainSculpt.TileSize"/> squared.</summary>
    /// <exception cref="ArgumentException"><paramref name="deltas"/> is the wrong length.</exception>
    public TerrainSculptTile(int tileX, int tileZ, float[] deltas)
    {
        ArgumentNullException.ThrowIfNull(deltas);
        int expected = TerrainSculpt.TileSize * TerrainSculpt.TileSize;
        if (deltas.Length != expected)
            throw new ArgumentException($"a sculpt tile needs exactly {expected} deltas, got {deltas.Length}.", nameof(deltas));
        TileX = tileX;
        TileZ = tileZ;
        Deltas = deltas;
    }
}
