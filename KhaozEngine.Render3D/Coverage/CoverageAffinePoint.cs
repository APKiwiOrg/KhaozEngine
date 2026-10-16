using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>An exact sum of up to four nonoverlapping double components. Value is an approximate view.</summary>
public readonly struct CoverageCoordinate
{
    readonly double c0, c1, c2, c3;
    internal CoverageCoordinate(ReadOnlySpan<double> values)
    {
        c0 = values.Length > 0 ? values[0] : 0;
        c1 = values.Length > 1 ? values[1] : 0;
        c2 = values.Length > 2 ? values[2] : 0;
        c3 = values.Length > 3 ? values[3] : 0;
    }
    public double this[int index] => index switch
    { 0 => c0, 1 => c1, 2 => c2, 3 => c3, _ => throw new ArgumentOutOfRangeException(nameof(index)) };
    public double Value => ((c0 + c1) + c2) + c3;
    public CoverageScalarBounds Bounds => CoverageIntervals.FromCoordinate(this);
    public static CoverageCoordinate FromDouble(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentException("Coordinate must be finite.", nameof(value));
        Span<double> values = stackalloc double[1] { value };
        return new CoverageCoordinate(values);
    }
}

/// <summary>An authoritative affine image of displayed source floats, without rounding the affine sum.</summary>
public readonly record struct CoverageAffinePoint(CoverageCoordinate X, CoverageCoordinate Y, CoverageCoordinate Z)
{
    public CoveragePoint3 Approximate => new(X.Value, Y.Value, Z.Value);
    public static CoverageAffinePoint FromDouble(double x, double y, double z = 0) =>
        new(CoverageCoordinate.FromDouble(x), CoverageCoordinate.FromDouble(y), CoverageCoordinate.FromDouble(z));

    /// <summary>Treat the supplied finite affine float matrix coefficients as exact. This does not bound matrix inversion error.</summary>
    public static CoverageAffinePoint Project(Vector3 point, in Matrix4x4 matrix)
    {
        ReadOnlySpan<float> values = stackalloc float[] {point.X, point.Y, point.Z, matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24, matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44 };
        foreach (float value in values)
            if (!float.IsFinite(value)) throw new ArgumentException("Projection inputs must be finite.");
        if (matrix.M14 != 0 || matrix.M24 != 0 || matrix.M34 != 0 || matrix.M44 != 1)
            throw new ArgumentException("Projection matrix must be affine.", nameof(matrix));
        return new(Coordinate(point, matrix.M11, matrix.M21, matrix.M31, matrix.M41),
            Coordinate(point, matrix.M12, matrix.M22, matrix.M32, matrix.M42),
            Coordinate(point, matrix.M13, matrix.M23, matrix.M33, matrix.M43));
    }

    static CoverageCoordinate Coordinate(Vector3 point, float x, float y, float z, float translation)
    {
        Span<double> expansion = stackalloc double[4];
        int length = 0;
        // Each float product has at most 48 significant bits and is exact in double.
        CoverageGeometry.Grow(expansion, ref length, (double)point.X * x);
        CoverageGeometry.Grow(expansion, ref length, (double)point.Y * y);
        CoverageGeometry.Grow(expansion, ref length, (double)point.Z * z);
        CoverageGeometry.Grow(expansion, ref length, translation);
        return new CoverageCoordinate(expansion[..length]);
    }
}
