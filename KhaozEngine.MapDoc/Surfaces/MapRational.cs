using System;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>A positive, reduced authored lattice unit. Default is invalid.</summary>
public readonly struct MapRational : IEquatable<MapRational>
{
    public int Numerator { get; }
    public int Denominator { get; }

    public MapRational(int numerator, int denominator)
    {
        if (numerator <= 0 || denominator <= 0) throw new MapDocumentException("unit parts must be positive");
        if (MapExactValue.Gcd(numerator, denominator) != 1) throw new MapDocumentException("unit must be reduced");
        Numerator = numerator;
        Denominator = denominator;
    }

    internal MapExactValue Exact()
    {
        if (Numerator <= 0 || Denominator <= 0) throw new MapDocumentException("unit parts must be positive");
        return new(Numerator, Denominator);
    }

    public bool Equals(MapRational other) => Numerator == other.Numerator && Denominator == other.Denominator;
    public override bool Equals(object? obj) => obj is MapRational other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Numerator, Denominator);
    public static bool operator ==(MapRational a, MapRational b) => a.Equals(b);
    public static bool operator !=(MapRational a, MapRational b) => !a.Equals(b);
}
