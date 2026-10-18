using System;
using System.Numerics;
using I = KhaozEngine.Render3D.CoverageFloatInterval;
using F = KhaozEngine.Render3D.CoverageFloatIntervals;
using V = KhaozEngine.Render3D.CoverageVertexEnclosure;

namespace KhaozEngine.Render3D;

/// <summary>Vector expansions of the pinned scalar graph. Copies are exact, arithmetic uses NC1.</summary>
internal static class CoverageEnclosureMath
{
    internal static V Point(Vector3 p) => new(F.Point(p.X), F.Point(p.Y), F.Point(p.Z));
    internal static V Unbounded => new(F.Unbounded, F.Unbounded, F.Unbounded);
    internal static bool Finite(V p) => p.X.IsFinite && p.Y.IsFinite && p.Z.IsFinite;
    internal static V Add(V a, V b) => new(F.Add(a.X, b.X), F.Add(a.Y, b.Y), F.Add(a.Z, b.Z));
    internal static V Subtract(V a, V b) => new(F.Subtract(a.X, b.X), F.Subtract(a.Y, b.Y), F.Subtract(a.Z, b.Z));
    internal static V Multiply(V a, I b) => new(F.Multiply(a.X, b), F.Multiply(a.Y, b), F.Multiply(a.Z, b));
    internal static V Divide(V a, I b) => new(F.Divide(a.X, b), F.Divide(a.Y, b), F.Divide(a.Z, b));
    internal static V Hull(V a, V b) => new(F.Hull(a.X, b.X), F.Hull(a.Y, b.Y), F.Hull(a.Z, b.Z));
    internal static V Store(V a) => new(F.Store32(a.X), F.Store32(a.Y), F.Store32(a.Z));
    internal static I Dot(V a, V b) => F.Add(F.Add(F.Multiply(a.X, b.X), F.Multiply(a.Y, b.Y)), F.Multiply(a.Z, b.Z));
    internal static V Cross(V a, V b) => new(
        F.Subtract(F.Multiply(a.Y, b.Z), F.Multiply(a.Z, b.Y)),
        F.Subtract(F.Multiply(a.Z, b.X), F.Multiply(a.X, b.Z)),
        F.Subtract(F.Multiply(a.X, b.Y), F.Multiply(a.Y, b.X)));

    // A square, a sum of squares and its square root are mathematically nonnegative, including flushing.
    internal static I Square(I x)
    {
        I product = F.Multiply(x, x);
        return product.IsFinite ? new(Math.Max(0, product.Min), product.Max) : F.Unbounded;
    }

    internal static I Length(V a) => Root(F.Add(F.Add(Square(a.X), Square(a.Y)), Square(a.Z)));
    internal static I Length2(I x, I y) => Root(F.Add(Square(x), Square(y)));
    internal static I Root(I squared) => squared.IsFinite ? F.Sqrt(new(Math.Max(0, squared.Min), squared.Max)) : F.Unbounded;

    internal static V Transform(V p, in Matrix4x4 m, bool direction = false) => new(
        Coordinate(p, m.M11, m.M21, m.M31, m.M41, direction),
        Coordinate(p, m.M12, m.M22, m.M32, m.M42, direction),
        Coordinate(p, m.M13, m.M23, m.M33, m.M43, direction));

    static I Coordinate(V p, float x, float y, float z, float translation, bool direction)
    {
        I linear = F.Add(F.Add(F.Multiply(p.X, F.Point(x)), F.Multiply(p.Y, F.Point(y))), F.Multiply(p.Z, F.Point(z)));
        return direction ? linear : F.Add(linear, F.Point(translation));
    }

    internal static V ExactCenter(Vector3 p, in Matrix4x4 m) => new(
        ExactCoordinate(p, m.M11, m.M21, m.M31, m.M41), ExactCoordinate(p, m.M12, m.M22, m.M32, m.M42),
        ExactCoordinate(p, m.M13, m.M23, m.M33, m.M43));

    static I ExactCoordinate(Vector3 p, float x, float y, float z, float t)
    {
        // Float products fit exactly in binary64. Outward endpoints also cover an independent rounded evaluation.
        var a = new CoverageScalarBounds(Math.BitDecrement((double)p.X * x), Math.BitIncrement((double)p.X * x));
        var b = new CoverageScalarBounds(Math.BitDecrement((double)p.Y * y), Math.BitIncrement((double)p.Y * y));
        var c = new CoverageScalarBounds(Math.BitDecrement((double)p.Z * z), Math.BitIncrement((double)p.Z * z));
        var result = CoverageIntervals.Add(CoverageIntervals.Add(CoverageIntervals.Add(a, b), c), new(t, t));
        return new(result.Min, result.Max);
    }

    internal static V Skin(in SkinnedVertex v, ReadOnlySpan<Matrix4x4> palette, bool direction = false, Vector3 input = default)
    {
        I total = F.Add(F.Add(F.Add(F.Point(v.BoneWeights.X), F.Point(v.BoneWeights.Y)), F.Point(v.BoneWeights.Z)), F.Point(v.BoneWeights.W));
        V p = Point(direction ? input : v.Position);
        V identity = Transform(p, Matrix4x4.Identity, direction);
        if (total.Max < 1e-8f) return identity;
        V result = new(SkinCoordinate(p, v, palette, 0, direction), SkinCoordinate(p, v, palette, 1, direction),
            SkinCoordinate(p, v, palette, 2, direction));
        return total.Min < 1e-8f ? Hull(identity, result) : result;
    }

    static I SkinCoordinate(V p, in SkinnedVertex v, ReadOnlySpan<Matrix4x4> palette, int column, bool direction)
    {
        I linear = F.Add(F.Add(F.Multiply(p.X, Blend(v, palette, column)), F.Multiply(p.Y, Blend(v, palette, 4 + column))),
            F.Multiply(p.Z, Blend(v, palette, 8 + column)));
        return direction ? linear : F.Add(linear, Blend(v, palette, 12 + column));
    }

    static I Blend(in SkinnedVertex v, ReadOnlySpan<Matrix4x4> palette, int component)
    {
        I result = F.Point(0);
        for (int i = 0; i < 4; i++)
            result = F.Add(result, F.Multiply(F.Point(Element(palette[(int)v.BoneIndices[i]], component)), F.Point(v.BoneWeights[i])));
        return result;
    }

    static float Element(in Matrix4x4 m, int i) => i switch
    {
        0 => m.M11,
        1 => m.M12,
        2 => m.M13,
        4 => m.M21,
        5 => m.M22,
        6 => m.M23,
        8 => m.M31,
        9 => m.M32,
        10 => m.M33,
        12 => m.M41,
        13 => m.M42,
        14 => m.M43,
        _ => throw new ArgumentOutOfRangeException(nameof(i))
    };

    internal static V Interpolate(in CoveragePositionRecipe r, ReadOnlySpan<V> source)
    {
        if (r.WeightA == 12) return source[r.A];
        if (r.WeightB == 12) return source[r.B];
        if (r.WeightC == 12) return source[r.C];
        if (r.WeightC == 0) return Edge(r.A, r.B, r.WeightB, source);
        if (r.WeightB == 0) return Edge(r.A, r.C, r.WeightC, source);
        if (r.WeightA == 0) return Edge(r.B, r.C, r.WeightC, source);
        return Divide(Add(Add(Multiply(source[r.A], F.Point(r.WeightA)), Multiply(source[r.B], F.Point(r.WeightB))),
            Multiply(source[r.C], F.Point(r.WeightC))), F.Point(12));
    }

    static V Edge(int a, int b, int numerator, ReadOnlySpan<V> source)
    {
        if (a > b) { (a, b) = (b, a); numerator = 12 - numerator; }
        // The ratio is a compiled constant, emitted once as binary32, not a device division.
        I t = F.Point(numerator / 12f);
        return Add(Multiply(source[a], F.Subtract(F.Point(1), t)), Multiply(source[b], t));
    }
}
