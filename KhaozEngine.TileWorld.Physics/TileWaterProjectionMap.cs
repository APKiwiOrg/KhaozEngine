using System;
using B = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Basis;
using I = KhaozEngine.TileWorld.Physics.TileWaterArithmetic;
using P = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Plane;
using V = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Vector;

namespace KhaozEngine.TileWorld.Physics;

// Constructed once with captured geometry. Evaluation does not consume these maps yet.
internal static class TileWaterProjectionMap
{
    internal static int[]? Build(TileWaterConvexShape parent, ReadOnlySpan<P> planes, ReadOnlySpan<B> bases)
    {
        ReadOnlySpan<P> prefix = parent.Planes;
        if (planes.Length < prefix.Length) return null;
        for (int i = 0; i < prefix.Length; i++)
            if (!Same(planes[i], prefix[i])) return null;

        var map = new int[bases.Length];
        Array.Fill(map, -1);
        ReadOnlySpan<B> parentBases = parent.Bases;
        for (int i = 0; i < bases.Length; i++)
        {
            B basis = bases[i];
            if (basis.A >= prefix.Length || basis.B >= prefix.Length || basis.C >= prefix.Length) continue;
            // Parent and face ordinals interleave differently once a face adds constraints.
            // Only the exact ordered plane indices identify identical projection operands.
            for (int j = 0; j < parentBases.Length; j++)
                if (basis == parentBases[j]) { map[i] = j; break; }
        }
        return map;
    }

    static bool Same(P a, P b) => a.HeightSensitive == b.HeightSensitive &&
        Same(a.Normal, b.Normal) && Same(a.Offset, b.Offset);
    static bool Same(V a, V b) => Same(a.X, b.X) && Same(a.Y, b.Y) && Same(a.Z, b.Z);
    static bool Same(I a, I b) => Bits(a.Low) == Bits(b.Low) && Bits(a.High) == Bits(b.High);
    static long Bits(double value) => BitConverter.DoubleToInt64Bits(value);
}
