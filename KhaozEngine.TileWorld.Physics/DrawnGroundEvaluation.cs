using System;
using System.Numerics;

namespace KhaozEngine.TileWorld.Physics;

internal interface IDrawnGroundNumber<T> where T : struct, IDrawnGroundNumber<T>
{
    static abstract T From(float value);
    static abstract T operator +(T a, T b);
    static abstract T operator -(T a, T b);
    static abstract T operator *(T a, T b);
    static abstract T Divide(T value, float divisor);
    static abstract T Clamp(T value, float minimum, float maximum);
}

/// <summary>One expression for actual values, represented ranges and roundoff certificates.</summary>
internal static class DrawnGroundEvaluation
{
    internal readonly record struct Scalar(float Value) : IDrawnGroundNumber<Scalar>
    {
        public static Scalar From(float value) => new(value);
        public static Scalar operator +(Scalar a, Scalar b) => new(a.Value + b.Value);
        public static Scalar operator -(Scalar a, Scalar b) => new(a.Value - b.Value);
        public static Scalar operator *(Scalar a, Scalar b) => new(a.Value * b.Value);
        public static Scalar Divide(Scalar value, float divisor) => new(value.Value / divisor);
        public static Scalar Clamp(Scalar value, float minimum, float maximum) => new(Math.Clamp(value.Value, minimum, maximum));
    }

    internal readonly record struct Range(float Low, float High) : IDrawnGroundNumber<Range>
    {
        public static Range From(float value) => new(value, value);
        public static Range operator +(Range a, Range b) => new(a.Low + b.Low, a.High + b.High);
        public static Range operator -(Range a, Range b) => new(a.Low - b.High, a.High - b.Low);
        public static Range operator *(Range a, Range b)
        {
            float p = a.Low * b.Low, q = a.Low * b.High, r = a.High * b.Low, s = a.High * b.High;
            return new(MathF.Min(MathF.Min(p, q), MathF.Min(r, s)), MathF.Max(MathF.Max(p, q), MathF.Max(r, s)));
        }
        public static Range Divide(Range value, float divisor) => divisor > 0
            ? new(value.Low / divisor, value.High / divisor) : new(value.High / divisor, value.Low / divisor);
        public static Range Clamp(Range value, float minimum, float maximum) =>
            new(Math.Clamp(value.Low, minimum, maximum), Math.Clamp(value.High, minimum, maximum));
    }

    internal static (T B, T C) Weights<T>(T x, T z, in DrawnGroundTriangle triangle) where T : struct, IDrawnGroundNumber<T>
    {
        Vector2 a = TileTriangulation.Local(triangle.PlanA), b = TileTriangulation.Local(triangle.PlanB),
            c = TileTriangulation.Local(triangle.PlanC);
        Vector2 ab = b - a, ac = c - a;
        T dx = x - T.From(a.X), dz = z - T.From(a.Y);
        float determinant = ab.X * ac.Y - ac.X * ab.Y;
        return (T.Divide(dx * T.From(ac.Y) - T.From(ac.X) * dz, determinant),
            T.Divide(T.From(ab.X) * dz - dx * T.From(ab.Y), determinant));
    }

    internal static T Height<T>(T b, T c, in DrawnGroundTriangle triangle) where T : struct, IDrawnGroundNumber<T>
    {
        T a = T.From(triangle.A.Y);
        T height = a + b * (T.From(triangle.B.Y) - a) + c * (T.From(triangle.C.Y) - a);
        // A barycentric point on a drawn triangle stays inside its vertex range, including rounded seams.
        return T.Clamp(height, MathF.Min(triangle.A.Y, MathF.Min(triangle.B.Y, triangle.C.Y)),
            MathF.Max(triangle.A.Y, MathF.Max(triangle.B.Y, triangle.C.Y)));
    }
}
