using System;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc;

/// <summary>One block of authored height deltas at a tile coordinate, the storage unit of
/// <see cref="MapTerrainOverrides"/>. A tile at (<see cref="TileX"/>, <see cref="TileZ"/>) covers global
/// cells [TileX * TileSize .. TileX * TileSize + TileSize - 1] on each axis, where TileSize is
/// <see cref="TerrainSculpt.TileSize"/>. Deltas are meters, row-major (index = localZ * TileSize + localX).</summary>
public sealed class MapSculptTile
{
    /// <summary>Tile X index.</summary>
    public int TileX { get; }

    /// <summary>Tile Z index.</summary>
    public int TileZ { get; }

    /// <summary>The row-major delta grid, length <see cref="TerrainSculpt.TileSize"/> squared, in meters.</summary>
    public float[] Deltas { get; }

    /// <summary>Creates a zeroed tile at the given tile coordinate.</summary>
    public MapSculptTile(int tileX, int tileZ)
        : this(tileX, tileZ, new float[TerrainSculpt.TileSize * TerrainSculpt.TileSize]) { }

    /// <summary>Wraps an existing delta array (length must be <see cref="TerrainSculpt.TileSize"/> squared),
    /// used by the loader when rebuilding a saved tile.</summary>
    /// <exception cref="ArgumentException"><paramref name="deltas"/> is the wrong length.</exception>
    public MapSculptTile(int tileX, int tileZ, float[] deltas)
    {
        ArgumentNullException.ThrowIfNull(deltas);
        int expected = TerrainSculpt.TileSize * TerrainSculpt.TileSize;
        if (deltas.Length != expected)
            throw new ArgumentException($"a sculpt tile needs exactly {expected} deltas, got {deltas.Length}.", nameof(deltas));
        TileX = tileX;
        TileZ = tileZ;
        Deltas = deltas;
    }

    /// <summary>Reads or writes the delta (meters) at a local cell (0 .. TileSize-1 on each axis).</summary>
    /// <exception cref="ArgumentOutOfRangeException">A local coordinate is outside the tile.</exception>
    public float this[int localX, int localZ]
    {
        get => Deltas[Index(localX, localZ)];
        set => Deltas[Index(localX, localZ)] = value;
    }

    static int Index(int localX, int localZ)
    {
        if ((uint)localX >= TerrainSculpt.TileSize || (uint)localZ >= TerrainSculpt.TileSize)
            throw new ArgumentOutOfRangeException(nameof(localX), $"local cell ({localX}, {localZ}) is outside the {TerrainSculpt.TileSize}-cell tile.");
        return localZ * TerrainSculpt.TileSize + localX;
    }
}
