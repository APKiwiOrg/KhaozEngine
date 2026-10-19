using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Exact represented coefficient and translation bit keys, without boxed interval equality.</summary>
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

    static bool Same(Vector3 a, Vector3 b) => BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X) &&
        BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y) &&
        BitConverter.SingleToInt32Bits(a.Z) == BitConverter.SingleToInt32Bits(b.Z);

    static void Add(ref HashCode hash, Vector3 vector)
    {
        hash.Add(BitConverter.SingleToInt32Bits(vector.X));
        hash.Add(BitConverter.SingleToInt32Bits(vector.Y));
        hash.Add(BitConverter.SingleToInt32Bits(vector.Z));
    }
}
