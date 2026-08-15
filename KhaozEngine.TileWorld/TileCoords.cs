using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld;

/// <summary>Address of one 64x64 region. <see cref="Of"/> floors, so negative world coordinates land in
/// negative regions with positive local coordinates.</summary>
public readonly record struct RegionCoord(int Rx, int Rz)
{
    /// <summary>The region holding world tile (worldX, worldZ).</summary>
    public static RegionCoord Of(int worldX, int worldZ) =>
        new(FloorDiv(worldX, TileRegion.Size), FloorDiv(worldZ, TileRegion.Size));

    /// <summary>World x of this region's west edge (local x 0).</summary>
    public int OriginX => Rx * TileRegion.Size;
    /// <summary>World z of this region's south edge (local z 0).</summary>
    public int OriginZ => Rz * TileRegion.Size;

    /// <summary>The region dx regions east and dz regions north of this one.</summary>
    public RegionCoord Offset(int dx, int dz) => new(Rx + dx, Rz + dz);

    /// <summary>The 64x64 world rect this region covers.</summary>
    public TileRect Rect => new(OriginX, OriginZ, TileRegion.Size, TileRegion.Size);

    internal static int FloorDiv(int a, int b)
    {
        int q = a / b;
        if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
        return q;
    }

    internal static int FloorMod(int a, int b)
    {
        int m = a % b;
        return m < 0 ? m + b : m;
    }

    /// <summary>Renders as (rx, rz), the form the error messages quote.</summary>
    public override string ToString() => $"({Rx}, {Rz})";
}

/// <summary>A world tile address: x east, z north, plane up.</summary>
public readonly record struct TileCoord(int X, int Z, int Plane)
{
    /// <summary>The region this tile falls in.</summary>
    public RegionCoord Region => RegionCoord.Of(X, Z);
    /// <summary>This tile's x within its region, always 0..63.</summary>
    public int LocalX => RegionCoord.FloorMod(X, TileRegion.Size);
    /// <summary>This tile's z within its region, always 0..63.</summary>
    public int LocalZ => RegionCoord.FloorMod(Z, TileRegion.Size);
    /// <summary>The tile dx east and dz north of this one, on the same plane.</summary>
    public TileCoord Offset(int dx, int dz) => new(X + dx, Z + dz, Plane);
    /// <summary>Renders as (x, z, pN).</summary>
    public override string ToString() => $"({X}, {Z}, p{Plane})";
}

/// <summary>An axis-aligned rect of world tiles, far edges EXCLUSIVE (<see cref="X1"/> is one past the last
/// column). Empty when either dimension is not positive.</summary>
public readonly record struct TileRect(int X, int Z, int Width, int Height)
{
    /// <summary>One past the last column, so the rect covers x in [X, X1).</summary>
    public int X1 => X + Width;
    /// <summary>One past the last row, so the rect covers z in [Z, Z1).</summary>
    public int Z1 => Z + Height;
    /// <summary>True when the rect covers no tiles at all.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;
    /// <summary>True when world tile (x, z) falls inside the rect.</summary>
    public bool Contains(int x, int z) => x >= X && x < X1 && z >= Z && z < Z1;

    /// <summary>The rect spanning two INCLUSIVE corners, given in any order.</summary>
    public static TileRect FromCorners(int x0, int z0, int x1, int z1)
    {
        int minX = Math.Min(x0, x1), maxX = Math.Max(x0, x1);
        int minZ = Math.Min(z0, z1), maxZ = Math.Max(z0, z1);
        return new TileRect(minX, minZ, maxX - minX + 1, maxZ - minZ + 1);
    }

    /// <summary>The rect grown by n tiles on every side (shrunk when n is negative).</summary>
    public TileRect Expand(int n) => new(X - n, Z - n, Width + 2 * n, Height + 2 * n);

    /// <summary>The overlap of the two rects, empty when they do not overlap.</summary>
    public TileRect Intersect(TileRect o)
    {
        int x0 = Math.Max(X, o.X), z0 = Math.Max(Z, o.Z);
        int x1 = Math.Min(X1, o.X1), z1 = Math.Min(Z1, o.Z1);
        return new TileRect(x0, z0, x1 - x0, z1 - z0);
    }

    /// <summary>The smallest rect covering both, ignoring an empty operand.</summary>
    public TileRect Union(TileRect o)
    {
        if (IsEmpty) return o;
        if (o.IsEmpty) return this;
        int x0 = Math.Min(X, o.X), z0 = Math.Min(Z, o.Z);
        int x1 = Math.Max(X1, o.X1), z1 = Math.Max(Z1, o.Z1);
        return new TileRect(x0, z0, x1 - x0, z1 - z0);
    }

    /// <summary>True when the two rects share at least one tile.</summary>
    public bool Intersects(TileRect o) => !Intersect(o).IsEmpty;
}

/// <summary>The eight step directions in the OSRS neighbour-expansion order the pathfinder relies on.</summary>
public enum TileDirection : byte { W = 0, E = 1, S = 2, N = 3, SW = 4, SE = 5, NW = 6, NE = 7 }

/// <summary>Helpers over <see cref="TileDirection"/>.</summary>
public static class TileDirections
{
    static readonly TileDirection[] AllDirections =
    {
        TileDirection.W, TileDirection.E, TileDirection.S, TileDirection.N,
        TileDirection.SW, TileDirection.SE, TileDirection.NW, TileDirection.NE,
    };

    /// <summary>The eight directions in the fixed OSRS expansion order. Read only, because the pathfinder's
    /// tie-breaking depends on this exact order.</summary>
    public static IReadOnlyList<TileDirection> All => AllDirections;

    /// <summary>The (dx, dz) step for one direction.</summary>
    public static (int Dx, int Dz) Delta(TileDirection d) => d switch
    {
        TileDirection.W => (-1, 0),
        TileDirection.E => (1, 0),
        TileDirection.S => (0, -1),
        TileDirection.N => (0, 1),
        TileDirection.SW => (-1, -1),
        TileDirection.SE => (1, -1),
        TileDirection.NW => (-1, 1),
        TileDirection.NE => (1, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(d)),
    };

    /// <summary>True for the four corner directions, which need both their cardinal neighbours clear.</summary>
    public static bool IsDiagonal(TileDirection d) => d >= TileDirection.SW;
}
