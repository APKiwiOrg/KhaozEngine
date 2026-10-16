using System;

namespace KhaozEngine.Render3D;

/// <summary>Certificates that two closed triangles are disjoint or meet only in their common topology. A supporting
/// plane restricts possible contact to extreme vertices. Intersecting those vertex masks over several planes cannot
/// remove a real contact. No supporting-plane proof means refusal, including existing defects and numerical ambiguity.</summary>
internal static class CoverageContact
{
    internal static bool OnlyCommonTopology(ReadOnlySpan<CoverageVector> a, ReadOnlySpan<CoverageVector> b,
        ReadOnlySpan<int> ag, ReadOnlySpan<int> bg)
    {
        int sharedA = 0, sharedB = 0, maskA = 7, maskB = 7;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                if (ag[i] == bg[j]) { sharedA |= 1 << i; sharedB |= 1 << j; }
        if (Axis(a, b, a[0], a[1], a[0], a[2], sharedA, sharedB, ref maskA, ref maskB)
            || Axis(a, b, b[0], b[1], b[0], b[2], sharedA, sharedB, ref maskA, ref maskB)) return true;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                if (Axis(a, b, a[i], a[(i + 1) % 3], b[j], b[(j + 1) % 3], sharedA, sharedB, ref maskA, ref maskB)) return true;
        // Edge cross coordinate axes supply in-plane supporting directions for coplanar triangles. Any direction is a
        // valid separating-axis proof. All three choices avoid assuming the plane has a particular dominant component.
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                CoverageVector unit = j switch { 0 => new(1, 0, 0), 1 => new(0, 1, 0), _ => new(0, 0, 1) };
                if (Axis(a, b, a[i], a[(i + 1) % 3], default, unit, sharedA, sharedB, ref maskA, ref maskB)
                    || Axis(a, b, b[i], b[(i + 1) % 3], default, unit, sharedA, sharedB, ref maskA, ref maskB)) return true;
            }
        return false;
    }

    static bool Axis(ReadOnlySpan<CoverageVector> a, ReadOnlySpan<CoverageVector> b, CoverageVector u0, CoverageVector u1,
        CoverageVector v0, CoverageVector v1, int sharedA, int sharedB, ref int maskA, ref int maskB)
    {
        bool positive = false, negative = false;
        int equalA = 0, equalB = 0;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                if (!CoverageExactTriple.TrySign(u0, u1, v0, v1, b[j], a[i], out int sign)) return false;
                positive |= sign > 0; negative |= sign < 0;
                if (positive && negative) return false;
                if (sign == 0) { equalA |= 1 << i; equalB |= 1 << j; }
            }
        if (!positive && !negative) return false;
        maskA &= equalA; maskB &= equalB;
        return (maskA & ~sharedA) == 0 || (maskB & ~sharedB) == 0;
    }
}

/// <summary>Exact sign of ((b-a) cross (d-c)) dot (f-e), using outward intervals first and an error-free expansion
/// fallback. Underflow or overflow in the exact fallback is unsupported, never resolved as zero.</summary>
internal static class CoverageExactTriple
{
    const double MinProduct = 2.2250738585072014e-308 * 9007199254740992d;

    internal static bool TrySign(CoverageVector a, CoverageVector b, CoverageVector c, CoverageVector d,
        CoverageVector e, CoverageVector f, out int sign)
    {
        CoverageScalarBounds bound = CoverageBoundedVector.Dot(CoverageBoundedVector.Cross(
            CoverageBoundedVector.From(b) - CoverageBoundedVector.From(a), CoverageBoundedVector.From(d) - CoverageBoundedVector.From(c)),
            CoverageBoundedVector.From(f) - CoverageBoundedVector.From(e));
        sign = 0;
        if (!bound.IsFinite) return false;
        if (bound.Min > 0) { sign = 1; return true; }
        if (bound.Max < 0) { sign = -1; return true; }
        if (bound == CoverageIntervals.Zero) return true;
        Span<double> differences = stackalloc double[18];
        Span<int> lengths = stackalloc int[9];
        for (int i = 0; i < 3; i++)
        {
            lengths[i] = Difference(Component(b, i), Component(a, i), differences.Slice(i * 2, 2));
            lengths[i + 3] = Difference(Component(d, i), Component(c, i), differences.Slice((i + 3) * 2, 2));
            lengths[i + 6] = Difference(Component(f, i), Component(e, i), differences.Slice((i + 6) * 2, 2));
        }
        Span<double> expansion = stackalloc double[256];
        int length = 0;
        for (int i = 0; i < 3; i++)
        {
            int j = (i + 1) % 3, k = (i + 2) % 3;
            if (!Term(differences, lengths, j, k + 3, i + 6, 1, expansion, ref length)
                || !Term(differences, lengths, k, j + 3, i + 6, -1, expansion, ref length)) return false;
        }
        for (int i = 0; i < length; i++)
        {
            if (!double.IsFinite(expansion[i])) return false;
            if (expansion[i] != 0) sign = Math.Sign(expansion[i]);
        }
        return true;
    }

    static double Component(CoverageVector v, int i) => i switch { 0 => v.X, 1 => v.Y, _ => v.Z };
    static int Difference(double a, double b, Span<double> expansion)
    {
        int length = 0;
        CoverageGeometry.Grow(expansion, ref length, a); CoverageGeometry.Grow(expansion, ref length, -b);
        return length;
    }

    static bool Term(ReadOnlySpan<double> values, ReadOnlySpan<int> lengths, int a, int b, int c, int sign,
        Span<double> expansion, ref int length)
    {
        foreach (double x in values.Slice(2 * a, lengths[a]))
            foreach (double y in values.Slice(2 * b, lengths[b]))
            {
                double product = x * y;
                if (!Safe(product, x, y)) return false;
                double tail = Math.FusedMultiplyAdd(x, y, -product);
                foreach (double z in values.Slice(2 * c, lengths[c]))
                    if (!AddProduct(product, z, sign, expansion, ref length) || !AddProduct(tail, z, sign, expansion, ref length)) return false;
            }
        return true;
    }

    static bool AddProduct(double a, double b, int sign, Span<double> expansion, ref int length)
    {
        if (a == 0 || b == 0) return true;
        double p = a * b;
        if (!Safe(p, a, b)) return false;
        CoverageGeometry.Grow(expansion, ref length, sign * Math.FusedMultiplyAdd(a, b, -p));
        CoverageGeometry.Grow(expansion, ref length, sign * p);
        return true;
    }
    static bool Safe(double product, double a, double b) => double.IsFinite(product) && (a == 0 || b == 0 || Math.Abs(product) >= MinProduct);
}
