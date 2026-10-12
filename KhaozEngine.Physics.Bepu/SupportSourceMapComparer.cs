using System;
using System.Collections.Generic;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Exact coefficient endpoint and represented translation keys, without boxed interval equality.</summary>
internal sealed class SupportSourceMapComparer : IEqualityComparer<SupportPolygonSourceMap>
{
    internal static readonly SupportSourceMapComparer Instance = new();

    public bool Equals(SupportPolygonSourceMap a, SupportPolygonSourceMap b) =>
        Same(a.X, b.X) && Same(a.Y, b.Y) && Same(a.Z, b.Z) &&
        BitConverter.SingleToInt32Bits(a.Translation.X) == BitConverter.SingleToInt32Bits(b.Translation.X) &&
        BitConverter.SingleToInt32Bits(a.Translation.Y) == BitConverter.SingleToInt32Bits(b.Translation.Y) &&
        BitConverter.SingleToInt32Bits(a.Translation.Z) == BitConverter.SingleToInt32Bits(b.Translation.Z);

    public int GetHashCode(SupportPolygonSourceMap map)
    {
        var hash = new HashCode();
        Add(ref hash, map.X);
        Add(ref hash, map.Y);
        Add(ref hash, map.Z);
        hash.Add(BitConverter.SingleToInt32Bits(map.Translation.X));
        hash.Add(BitConverter.SingleToInt32Bits(map.Translation.Y));
        hash.Add(BitConverter.SingleToInt32Bits(map.Translation.Z));
        return hash.ToHashCode();
    }

    static bool Same(GeometryVector a, GeometryVector b) => Same(a.X, b.X) && Same(a.Y, b.Y) && Same(a.Z, b.Z);
    static bool Same(GeometryInterval a, GeometryInterval b) => a.IsResolved == b.IsResolved &&
        BitConverter.DoubleToInt64Bits(a.Lower) == BitConverter.DoubleToInt64Bits(b.Lower) &&
        BitConverter.DoubleToInt64Bits(a.Upper) == BitConverter.DoubleToInt64Bits(b.Upper);

    static void Add(ref HashCode hash, GeometryVector vector)
    {
        Add(ref hash, vector.X);
        Add(ref hash, vector.Y);
        Add(ref hash, vector.Z);
    }

    static void Add(ref HashCode hash, GeometryInterval interval)
    {
        hash.Add(interval.IsResolved);
        hash.Add(BitConverter.DoubleToInt64Bits(interval.Lower));
        hash.Add(BitConverter.DoubleToInt64Bits(interval.Upper));
    }
}
