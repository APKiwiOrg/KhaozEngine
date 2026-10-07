using System;

namespace KhaozEngine.MapDoc.Surfaces;

public enum MapRowDirection : byte { PositiveZ = 0, NegativeZ = 1 }
public enum MapHeightDatum : byte { WorldY0 = 0 }

/// <summary>The exact conversion between authored lattice units and the shared world datum.</summary>
public sealed record MapLatticeFrame(MapRational CellUnitMetres, MapRational HeightUnitMetres,
    MapRowDirection RowDirection, MapHeightDatum Datum)
{
    public static MapLatticeFrame ImportedMetreCentimetre { get; } =
        new(new(1, 1), new(1, 100), MapRowDirection.NegativeZ, MapHeightDatum.WorldY0);

    void RequireValid()
    {
        _ = CellUnitMetres.Exact();
        _ = HeightUnitMetres.Exact();
        if (RowDirection is not (MapRowDirection.PositiveZ or MapRowDirection.NegativeZ) || Datum != MapHeightDatum.WorldY0)
            throw new MapDocumentException("invalid lattice frame");
    }

    public MapExactXz WorldXz(MapLatticeAddress address)
    {
        RequireValid();
        address.RequireValid();
        MapExactValue unit = CellUnitMetres.Exact();
        MapExactValue z = new MapExactValue(address.Z, address.Denominator).Multiply(unit);
        return new(new MapExactValue(address.X, address.Denominator).Multiply(unit),
            RowDirection == MapRowDirection.NegativeZ ? z.Negate() : z);
    }
    public MapExactValue Metres(MapExactValue heightUnits)
    {
        RequireValid();
        return heightUnits.Multiply(HeightUnitMetres.Exact());
    }
    public MapExactPoint Corner(long x, long z, int heightUnits)
    {
        MapExactXz xz = WorldXz(MapLatticeAddress.Corner(x, z));
        return new(xz.X, Metres(new(heightUnits, 1)), xz.Z);
    }
    public MapLatticeAddress AddressOf(MapExactValue worldX, MapExactValue worldZ)
    {
        RequireValid();
        MapExactValue x = worldX.Divide(CellUnitMetres.Exact());
        MapExactValue z = worldZ.Divide(CellUnitMetres.Exact());
        if (RowDirection == MapRowDirection.NegativeZ) z = z.Negate();
        Int128 d = (Int128)x.Denominator / MapExactValue.Gcd(x.Denominator, z.Denominator) * z.Denominator;
        if (d > MapLatticeAddress.MaxDenominator) throw new MapDocumentException("address denominator exceeds 64");
        Int128 nx = (Int128)x.Numerator * (d / x.Denominator);
        Int128 nz = (Int128)z.Numerator * (d / z.Denominator);
        if (nx < long.MinValue || nx > long.MaxValue || nz < long.MinValue || nz > long.MaxValue)
            throw new MapExactOverflowException();
        return MapLatticeAddress.Create((long)nx, (long)nz, (int)d);
    }
}
