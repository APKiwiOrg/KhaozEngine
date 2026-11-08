using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Double-precision 3-vector for certification arithmetic over float inputs.</summary>
internal readonly record struct CoverageVector(double X, double Y, double Z)
{
    internal static CoverageVector From(Vector3 v) => new(v.X, v.Y, v.Z);
    internal double Length => Math.Sqrt(Dot(this, this));
    internal double MaxAbs => Math.Max(Math.Abs(X), Math.Max(Math.Abs(Y), Math.Abs(Z)));
    internal static double Dot(in CoverageVector a, in CoverageVector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    internal static CoverageVector Cross(in CoverageVector a, in CoverageVector b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static CoverageVector operator +(CoverageVector a, CoverageVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static CoverageVector operator -(CoverageVector a, CoverageVector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static CoverageVector operator *(CoverageVector a, double s) => new(a.X * s, a.Y * s, a.Z * s);
}
