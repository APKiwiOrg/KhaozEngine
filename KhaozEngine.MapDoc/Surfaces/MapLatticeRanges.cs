using System;
using KhaozEngine.MapDoc.Storage;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>Exact positive-area footprint and bound ranges, independent of storage packing.</summary>
public static class MapLatticeRanges
{
    public static MapExactXz[] CellRect(MapLatticeFrame frame, MapPatchKey lattice, int slotCell)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (string.IsNullOrWhiteSpace(lattice.SurfaceId) || slotCell is < 0 or >= 4096)
            throw new MapDocumentException("invalid lattice cell");
        try
        {
            long x = checked(lattice.SlotX * 64 + slotCell % 64), z = checked(lattice.SlotZ * 64 + slotCell / 64);
            MapExactXz a = frame.WorldXz(MapLatticeAddress.Corner(x, z));
            MapExactXz b = frame.WorldXz(MapLatticeAddress.Corner(checked(x + 1), checked(z + 1)));
            return a.Z.CompareTo(b.Z) <= 0 ? new[] { a, b } : new[] { new MapExactXz(a.X, b.Z), new MapExactXz(b.X, a.Z) };
        }
        catch (OverflowException) { throw new MapExactOverflowException(); }
    }

    public static MapCellRect CellRange(MapLatticeFrame frame, MapExactXz min, MapExactXz max)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _ = frame.WorldXz(MapLatticeAddress.Corner(0, 0));
        if (min.X.CompareTo(max.X) > 0 || min.Z.CompareTo(max.Z) > 0)
            throw new MapDocumentException("invalid exact rectangle");
        MapExactValue unit = frame.CellUnitMetres.Exact();
        MapExactValue minZ = min.Z, maxZ = max.Z;
        if (frame.RowDirection == MapRowDirection.NegativeZ) (minZ, maxZ) = (maxZ.Negate(), minZ.Negate());
        long x0 = min.X.Divide(unit).Floor(), z0 = minZ.Divide(unit).Floor();
        if (min.X == max.X || min.Z == max.Z) return new(x0, z0, x0, z0);
        return new(x0, z0, max.X.Divide(unit).Ceiling(), maxZ.Divide(unit).Ceiling());
    }
}
