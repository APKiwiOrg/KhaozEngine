using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Double affine map for row vectors, p' = p * L + T, built only from finite affine float matrices.</summary>
internal readonly struct CoverageAffine
{
    readonly double m11, m12, m13, m21, m22, m23, m31, m32, m33, t1, t2, t3;

    CoverageAffine(double m11, double m12, double m13, double m21, double m22, double m23, double m31, double m32, double m33,
        double t1, double t2, double t3)
    {
        this.m11 = m11; this.m12 = m12; this.m13 = m13; this.m21 = m21; this.m22 = m22; this.m23 = m23;
        this.m31 = m31; this.m32 = m32; this.m33 = m33; this.t1 = t1; this.t2 = t2; this.t3 = t3;
    }

    internal static bool TryFrom(in Matrix4x4 m, out CoverageAffine affine)
    {
        affine = default;
        if (m.M14 != 0 || m.M24 != 0 || m.M34 != 0 || m.M44 != 1) return false;
        if (!float.IsFinite(m.M11) || !float.IsFinite(m.M12) || !float.IsFinite(m.M13) || !float.IsFinite(m.M21)
            || !float.IsFinite(m.M22) || !float.IsFinite(m.M23) || !float.IsFinite(m.M31) || !float.IsFinite(m.M32)
            || !float.IsFinite(m.M33) || !float.IsFinite(m.M41) || !float.IsFinite(m.M42) || !float.IsFinite(m.M43)) return false;
        affine = new(m.M11, m.M12, m.M13, m.M21, m.M22, m.M23, m.M31, m.M32, m.M33, m.M41, m.M42, m.M43);
        return true;
    }

    internal double Determinant => m11 * (m22 * m33 - m23 * m32) - m12 * (m21 * m33 - m23 * m31) + m13 * (m21 * m32 - m22 * m31);
    internal double Frobenius => Math.Sqrt(m11 * m11 + m12 * m12 + m13 * m13 + m21 * m21 + m22 * m22 + m23 * m23 + m31 * m31 + m32 * m32 + m33 * m33);
    /// <summary>Bound of |p * L| in the max norm per unit max norm of p, the largest column absolute sum.</summary>
    internal double MaxGain => Math.Max(Math.Abs(m11) + Math.Abs(m21) + Math.Abs(m31),
        Math.Max(Math.Abs(m12) + Math.Abs(m22) + Math.Abs(m32), Math.Abs(m13) + Math.Abs(m23) + Math.Abs(m33)));
    /// <summary>Certified upper bound of the linear part's spectral norm. The largest absolute row sum of the row Gram
    /// matrix bounds its largest eigenvalue, and is exact for any scaled rotation.</summary>
    internal double SpectralBound
    {
        get
        {
            CoverageVector r1 = Row(0), r2 = Row(1), r3 = Row(2);
            double g11 = CoverageVector.Dot(r1, r1), g22 = CoverageVector.Dot(r2, r2), g33 = CoverageVector.Dot(r3, r3);
            double g12 = Math.Abs(CoverageVector.Dot(r1, r2)), g13 = Math.Abs(CoverageVector.Dot(r1, r3)), g23 = Math.Abs(CoverageVector.Dot(r2, r3));
            return Math.Sqrt(Math.Max(g11 + g12 + g13, Math.Max(g12 + g22 + g23, g13 + g23 + g33)) * (1 + 1e-12));
        }
    }
    internal CoverageVector Translation => new(t1, t2, t3);
    internal CoverageVector Row(int row) => row switch { 0 => new(m11, m12, m13), 1 => new(m21, m22, m23), _ => new(m31, m32, m33) };
    /// <summary>Euclidean length of a column of the linear part.</summary>
    internal double ColumnLength(int column) => column switch
    {
        0 => Math.Sqrt(m11 * m11 + m21 * m21 + m31 * m31),
        1 => Math.Sqrt(m12 * m12 + m22 * m22 + m32 * m32),
        _ => Math.Sqrt(m13 * m13 + m23 * m23 + m33 * m33),
    };

    internal CoverageVector Apply(in CoverageVector p) => new(p.X * m11 + p.Y * m21 + p.Z * m31 + t1,
        p.X * m12 + p.Y * m22 + p.Z * m32 + t2, p.X * m13 + p.Y * m23 + p.Z * m33 + t3);
    internal CoverageVector Apply(Vector3 p) => Apply(CoverageVector.From(p));
    internal CoverageVector ApplyLinear(in CoverageVector p) => new(p.X * m11 + p.Y * m21 + p.Z * m31,
        p.X * m12 + p.Y * m22 + p.Z * m32, p.X * m13 + p.Y * m23 + p.Z * m33);
    internal CoverageAffine WithTranslation(in CoverageVector t) => new(m11, m12, m13, m21, m22, m23, m31, m32, m33, t.X, t.Y, t.Z);

    internal bool TryInverse(out CoverageAffine inverse)
    {
        inverse = default;
        double det = Determinant;
        if (!(det != 0) || !double.IsFinite(det)) return false;
        double i = 1 / det;
        double a11 = (m22 * m33 - m23 * m32) * i, a12 = (m13 * m32 - m12 * m33) * i, a13 = (m12 * m23 - m13 * m22) * i;
        double a21 = (m23 * m31 - m21 * m33) * i, a22 = (m11 * m33 - m13 * m31) * i, a23 = (m13 * m21 - m11 * m23) * i;
        double a31 = (m21 * m32 - m22 * m31) * i, a32 = (m12 * m31 - m11 * m32) * i, a33 = (m11 * m22 - m12 * m21) * i;
        var linear = new CoverageAffine(a11, a12, a13, a21, a22, a23, a31, a32, a33, 0, 0, 0);
        CoverageVector t = linear.ApplyLinear(Translation);
        inverse = linear.WithTranslation(new(-t.X, -t.Y, -t.Z));
        return double.IsFinite(inverse.Frobenius) && double.IsFinite(t.X) && double.IsFinite(t.Y) && double.IsFinite(t.Z);
    }

    /// <summary>The map p * a * b.</summary>
    internal static CoverageAffine Multiply(in CoverageAffine a, in CoverageAffine b)
    {
        CoverageVector r1 = b.ApplyLinear(a.Row(0)), r2 = b.ApplyLinear(a.Row(1)), r3 = b.ApplyLinear(a.Row(2));
        CoverageVector t = b.Apply(a.Translation);
        return new(r1.X, r1.Y, r1.Z, r2.X, r2.Y, r2.Z, r3.X, r3.Y, r3.Z, t.X, t.Y, t.Z);
    }

    /// <summary>Max-norm gain of the linear difference from a float matrix, and its largest translation difference.</summary>
    internal static double Deviation(in CoverageAffine a, in Matrix4x4 m, double magnitude)
    {
        double c1 = Math.Abs(a.m11 - m.M11) + Math.Abs(a.m21 - m.M21) + Math.Abs(a.m31 - m.M31);
        double c2 = Math.Abs(a.m12 - m.M12) + Math.Abs(a.m22 - m.M22) + Math.Abs(a.m32 - m.M32);
        double c3 = Math.Abs(a.m13 - m.M13) + Math.Abs(a.m23 - m.M23) + Math.Abs(a.m33 - m.M33);
        double t = Math.Max(Math.Abs(a.t1 - m.M41), Math.Max(Math.Abs(a.t2 - m.M42), Math.Abs(a.t3 - m.M43)));
        return magnitude * Math.Max(c1, Math.Max(c2, c3)) + t;
    }
}
