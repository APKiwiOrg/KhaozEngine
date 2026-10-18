using System;

namespace KhaozEngine.Render3D;

internal enum CoverageIntervalTruth { False, True, Either }

/// <summary>Binary64 outward endpoints enclosing the NC1 binary32 operation graph, including input flushing.</summary>
internal readonly record struct CoverageFloatInterval(double Min, double Max)
{
    internal bool IsFinite => double.IsFinite(Min) && double.IsFinite(Max) && Min <= Max;
    internal bool Contains(double value) => !double.IsNaN(value) && Min <= value && value <= Max;
}

internal static class CoverageFloatIntervals
{
    internal static CoverageFloatInterval Point(float value) => float.IsFinite(value) ? new(value, value) : Unbounded;
    internal static CoverageFloatInterval Unbounded => new(double.NegativeInfinity, double.PositiveInfinity);

    internal static CoverageFloatInterval Add(CoverageFloatInterval a, CoverageFloatInterval b)
    {
        a = Flush(a); b = Flush(b);
        if (!a.IsFinite || !b.IsFinite) return Unbounded;
        return Widen(Math.BitDecrement(a.Min + b.Min), Math.BitIncrement(a.Max + b.Max), CoverageReferencePrecision.BasicUlps);
    }

    internal static CoverageFloatInterval Subtract(CoverageFloatInterval a, CoverageFloatInterval b)
    {
        a = Flush(a); b = Flush(b);
        if (!a.IsFinite || !b.IsFinite) return Unbounded;
        return Widen(Math.BitDecrement(a.Min - b.Max), Math.BitIncrement(a.Max - b.Min), CoverageReferencePrecision.BasicUlps);
    }

    internal static CoverageFloatInterval Multiply(CoverageFloatInterval a, CoverageFloatInterval b)
    {
        a = Flush(a); b = Flush(b);
        if (!a.IsFinite || !b.IsFinite) return Unbounded;
        return Products(a.Min * b.Min, a.Min * b.Max, a.Max * b.Min, a.Max * b.Max, CoverageReferencePrecision.BasicUlps);
    }

    internal static CoverageFloatInterval Divide(CoverageFloatInterval a, CoverageFloatInterval b)
    {
        a = Flush(a); b = Flush(b);
        if (!a.IsFinite || !Divisor(b)) return Unbounded;
        var direct = Products(a.Min / b.Min, a.Min / b.Max, a.Max / b.Min, a.Max / b.Max, CoverageReferencePrecision.DivideUlps);
        return Hull(direct, Multiply(a, Reciprocal1(b)));
    }

    internal static CoverageFloatInterval Reciprocal1(CoverageFloatInterval value)
    {
        value = Flush(value);
        if (!Divisor(value)) return Unbounded;
        return Widen(Math.BitDecrement(1 / value.Max), Math.BitIncrement(1 / value.Min), 1);
    }

    internal static CoverageFloatInterval InverseSqrt2(CoverageFloatInterval value)
    {
        value = Flush(value);
        if (!value.IsFinite || value.Min < CoverageReferencePrecision.MinNormal32) return Unbounded;
        // The inherited instruction is bounded around its mathematical inverse square root, not a rounded sqrt.
        double lowRoot = Math.BitDecrement(Math.Sqrt(value.Min)), highRoot = Math.BitIncrement(Math.Sqrt(value.Max));
        return Widen(Math.BitDecrement(1 / highRoot), Math.BitIncrement(1 / lowRoot), 2);
    }

    internal static CoverageFloatInterval Sqrt(CoverageFloatInterval value)
    {
        value = Flush(value);
        if (!value.IsFinite || value.Min < 0) return Unbounded;
        if (value.Max == 0) return new(0, 0);
        double lo = value.Min == 0 ? 0 : Math.BitDecrement(Math.Sqrt(value.Min)), hi = Math.BitIncrement(Math.Sqrt(value.Max));
        var direct = Widen(lo, hi, CoverageReferencePrecision.SqrtUlps);
        if (!direct.IsFinite) return Unbounded;
        // RV1 keeps sqrt(0) and the preserved or flushed subnormal part finite and nonnegative.
        double relative = CoverageReferencePrecision.SqrtUlps * Math.ScaleB(1, -23);
        double lower = value.Min < CoverageReferencePrecision.MinNormal32 ? 0 : Math.BitDecrement(lo * (1 - relative));
        double upper = Math.BitIncrement(hi * (1 + relative));
        var result = Hull(new(Math.Max(0, direct.Min), direct.Max), new(lower, upper));
        if (value.Max >= CoverageReferencePrecision.MinNormal32)
        {
            double start = Math.Max(value.Min, CoverageReferencePrecision.MinNormal32), radius = 0;
            double idealLo = Math.BitDecrement(Math.Sqrt(start)), idealHi = Math.BitIncrement(Math.Sqrt(value.Max));
            // A wide inverse-root range would smear its largest ULP across its small endpoint and introduce
            // a spurious zero divisor. Evaluate each normal binade with the same operations and join its error.
            do
            {
                double end = Math.Min(value.Max, Math.ScaleB(1, Math.ILogB(start) + 1));
                var inherited = Divide(Point(1), InverseSqrt2(new(start, end)));
                if (!inherited.IsFinite) return Unbounded;
                double segmentLo = Math.BitDecrement(Math.Sqrt(start)), segmentHi = Math.BitIncrement(Math.Sqrt(end));
                radius = Math.Max(radius, Math.Max(Math.BitIncrement(segmentLo - inherited.Min), Math.BitIncrement(inherited.Max - segmentHi)));
                start = end;
            } while (start < value.Max);
            var symmetric = new CoverageFloatInterval(Math.Max(0, Math.BitDecrement(idealLo - radius)), Math.BitIncrement(idealHi + radius));
            result = Hull(result, symmetric);
        }
        return Flush(result);
    }

    internal static CoverageFloatInterval Store32(CoverageFloatInterval value)
    {
        value = Flush(value);
        return value.IsFinite ? Widen(value.Min, value.Max, CoverageReferencePrecision.StoreUlps) : Unbounded;
    }

    internal static CoverageFloatInterval Abs(CoverageFloatInterval value)
    {
        value = Flush(value);
        if (!value.IsFinite) return Unbounded;
        return new(value.Min <= 0 && value.Max >= 0 ? 0 : Math.Min(Math.Abs(value.Min), Math.Abs(value.Max)), Math.Max(Math.Abs(value.Min), Math.Abs(value.Max)));
    }

    internal static CoverageFloatInterval Negate(CoverageFloatInterval value)
    {
        value = Flush(value);
        return value.IsFinite ? new(-value.Max, -value.Min) : Unbounded;
    }

    internal static CoverageFloatInterval Min(CoverageFloatInterval a, CoverageFloatInterval b)
    {
        a = Flush(a); b = Flush(b);
        return a.IsFinite && b.IsFinite ? new(Math.Min(a.Min, b.Min), Math.Min(a.Max, b.Max)) : Unbounded;
    }

    internal static CoverageFloatInterval Max(CoverageFloatInterval a, CoverageFloatInterval b)
    {
        a = Flush(a); b = Flush(b);
        return a.IsFinite && b.IsFinite ? new(Math.Max(a.Min, b.Min), Math.Max(a.Max, b.Max)) : Unbounded;
    }

    internal static CoverageFloatInterval Hull(CoverageFloatInterval a, CoverageFloatInterval b)
    {
        a = Flush(a); b = Flush(b);
        return a.IsFinite && b.IsFinite ? new(Math.Min(a.Min, b.Min), Math.Max(a.Max, b.Max)) : Unbounded;
    }

    internal static CoverageFloatInterval Clamp(CoverageFloatInterval value, float min, float max) =>
        float.IsFinite(min) && float.IsFinite(max) && min <= max ? Min(Max(value, Point(min)), Point(max)) : Unbounded;

    internal static CoverageIntervalTruth CompareLess(CoverageFloatInterval a, CoverageFloatInterval b)
    {
        a = Flush(a); b = Flush(b);
        if (!a.IsFinite || !b.IsFinite) return CoverageIntervalTruth.Either;
        return a.Max < b.Min ? CoverageIntervalTruth.True : a.Min >= b.Max ? CoverageIntervalTruth.False : CoverageIntervalTruth.Either;
    }

    static bool Divisor(CoverageFloatInterval value) => value.IsFinite && (value.Min > 0 || value.Max < 0)
        && Math.Min(Math.Abs(value.Min), Math.Abs(value.Max)) >= CoverageReferencePrecision.MinNormal32
        && Math.Max(Math.Abs(value.Min), Math.Abs(value.Max)) <= CoverageReferencePrecision.MaxDivisor32;

    static CoverageFloatInterval Products(double a, double b, double c, double d, int ulps) => Widen(
        Math.BitDecrement(Math.Min(Math.Min(a, b), Math.Min(c, d))), Math.BitIncrement(Math.Max(Math.Max(a, b), Math.Max(c, d))), ulps);

    static CoverageFloatInterval Widen(double min, double max, int ulps)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max) || min < -float.MaxValue || max > float.MaxValue) return Unbounded;
        double magnitude = Math.Max(Math.Abs(min), Math.Abs(max));
        int exponent = magnitude == 0 ? -149 : Math.Max(-149, Math.ILogB(magnitude) - 23);
        double error = Math.BitIncrement(ulps * Math.ScaleB(1, exponent) + CoverageReferencePrecision.MinNormal32);
        return Flush(new(Math.BitDecrement(min - error), Math.BitIncrement(max + error)));
    }

    static CoverageFloatInterval Flush(CoverageFloatInterval value)
    {
        if (!value.IsFinite || value.Min < -float.MaxValue || value.Max > float.MaxValue) return Unbounded;
        double normal = CoverageReferencePrecision.MinNormal32;
        bool subnormal = value.Min < normal && value.Max > -normal;
        return subnormal ? new(Math.Min(value.Min, 0), Math.Max(value.Max, 0)) : value;
    }
}
