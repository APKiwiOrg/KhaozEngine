using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

public sealed partial class OrdinaryMovementEnvironment
{
    /// <summary>The exact sign of a + b + c + d for binary32 operands, through a nonoverlapping
    /// binary64 expansion. Its largest nonzero component carries the sign of the whole sum.</summary>
    internal static int ExactSign(float a, float b, float c, float d = 0)
        => ExactSign(stackalloc float[4] { a, b, c, d });

    /// <summary>The exact sign of a finite sum of binary32 operands, retaining small cancelled addends.</summary>
    internal static int ExactSign(ReadOnlySpan<float> values)
    {
        Span<double> expansion = stackalloc double[values.Length];
        int count = 0;
        foreach (float value in values) Grow(expansion, ref count, value);
        for (int i = count - 1; i >= 0; i--)
            if (expansion[i] != 0) return Math.Sign(expansion[i]);
        return 0;
    }

    static void Grow(Span<double> expansion, ref int count, double value)
    {
        double carry = value;
        for (int i = 0; i < count; i++)
        {
            double sum = carry + expansion[i];
            double virtualPart = sum - carry;
            expansion[i] = (carry - (sum - virtualPart)) + (expansion[i] - virtualPart);
            carry = sum;
        }
        expansion[count++] = carry;
    }

    internal static Vector3 Translate(Vector3 value, Vector3 origin, out double error)
    {
        double x = (double)value.X + origin.X, y = (double)value.Y + origin.Y, z = (double)value.Z + origin.Z;
        Vector3 translated = new((float)x, (float)y, (float)z);
        if (!MovementEnvironmentValidation.Finite(translated)) throw new ArgumentException("The translated coordinates must be finite.");
        error = Math.Sqrt((translated.X - x) * (translated.X - x) + (translated.Y - y) * (translated.Y - y) +
            (translated.Z - z) * (translated.Z - z));
        return translated;
    }

    /// <summary>The binary64 sum of a local and an origin coordinate, adding its rounding residual to
    /// <paramref name="error"/>. The residual is zero whenever the two binary32 operands fit one binary64.</summary>
    internal static double Absolute(float local, float origin, ref double error)
    {
        double sum = (double)local + origin, originPart = sum - local;
        error += Math.Abs(((double)local - (sum - originPart)) + ((double)origin - originPart));
        return sum;
    }

    /// <summary>An absolute interval in local coordinates, rounded outward so it contains every local height the
    /// absolute interval contains. The error is the largest displacement of a reported height.</summary>
    internal static bool TryToLocal(in MovementWaterInterval interval, float originY, out MovementWaterInterval local,
        out double error)
    {
        float lower = Outward(interval.LowerY, originY, false), upper = Outward(interval.UpperY, originY, true),
            surface = Outward(interval.NominalSurfaceY, originY, true);
        local = default;
        error = Math.Max(Displacement(lower, interval.LowerY, originY), Math.Max(Displacement(upper, interval.UpperY, originY),
            Displacement(surface, interval.NominalSurfaceY, originY)));
        if (!float.IsFinite(lower) || !float.IsFinite(upper) || !float.IsFinite(surface) || !(lower < upper)) return false;
        local = new(lower, upper, surface, interval.UpperIsFreeSurface, interval.LowerBoundaryId, interval.UpperBoundaryId);
        return true;
    }

    static float Outward(float value, float origin, bool up)
    {
        float result = (float)((double)value - origin);
        if (!float.IsFinite(result)) return result;
        int side = ExactSign(result, -value, origin);
        if (up && side < 0) return MathF.BitIncrement(result);
        return !up && side > 0 ? MathF.BitDecrement(result) : result;
    }

    static double Displacement(float local, float value, float origin) => Math.Abs((double)local - ((double)value - origin));
}
