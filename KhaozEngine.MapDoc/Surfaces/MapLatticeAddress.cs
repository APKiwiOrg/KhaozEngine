using System;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>A reduced common-denominator address in one surface lattice. Default is invalid.</summary>
public readonly struct MapLatticeAddress : IEquatable<MapLatticeAddress>, IComparable<MapLatticeAddress>
{
    public const int MaxDenominator = 64;
    public long X { get; }
    public long Z { get; }
    public int Denominator { get; }
    MapLatticeAddress(long x, long z, int denominator) { X = x; Z = z; Denominator = denominator; }

    public static MapLatticeAddress Create(long x, long z, int denominator)
    {
        if (denominator < 1) throw new MapDocumentException("address denominator must be positive");
        Int128 gcd = MapExactValue.Gcd(MapExactValue.Gcd(x, z), denominator);
        denominator /= (int)gcd;
        if (denominator > MaxDenominator) throw new MapDocumentException("address denominator exceeds 64");
        return new((long)((Int128)x / gcd), (long)((Int128)z / gcd), denominator);
    }

    public static MapLatticeAddress Corner(long x, long z) => new(x, z, 1);
    internal void RequireValid()
    {
        if (Denominator is < 1 or > MaxDenominator) throw new MapDocumentException("invalid address denominator");
    }
    public int CompareTo(MapLatticeAddress other)
    {
        RequireValid();
        other.RequireValid();
        int z = ((Int128)Z * other.Denominator).CompareTo((Int128)other.Z * Denominator);
        return z != 0 ? z : ((Int128)X * other.Denominator).CompareTo((Int128)other.X * Denominator);
    }
    public bool Equals(MapLatticeAddress other) => X == other.X && Z == other.Z && Denominator == other.Denominator;
    public override bool Equals(object? obj) => obj is MapLatticeAddress other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(X, Z, Denominator);
    public static bool operator ==(MapLatticeAddress a, MapLatticeAddress b) => a.Equals(b);
    public static bool operator !=(MapLatticeAddress a, MapLatticeAddress b) => !a.Equals(b);
}
