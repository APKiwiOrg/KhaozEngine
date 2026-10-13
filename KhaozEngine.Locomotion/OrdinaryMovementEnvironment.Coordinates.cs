using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

public sealed partial class OrdinaryMovementEnvironment
{
    /// <summary>The exact sign of a + b + c + d for binary32 operands, through a nonoverlapping
    /// binary64 expansion. Its largest nonzero component carries the sign of the whole sum.</summary>
    internal static int ExactSign(float a, float b, float c, float d = 0)
    {
        Span<double> expansion = stackalloc double[4];
        int count = 0;
        Grow(expansion, ref count, a);
        Grow(expansion, ref count, b);
        Grow(expansion, ref count, c);
        Grow(expansion, ref count, d);
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

    internal static MovementWaterInterval Translate(in MovementWaterInterval interval, float originY, out double error)
    {
        double lower = (double)interval.LowerY + originY, upper = (double)interval.UpperY + originY,
            surface = (double)interval.NominalSurfaceY + originY;
        float l = (float)lower, u = (float)upper, s = (float)surface;
        error = Math.Max(Math.Abs(l - lower), Math.Max(Math.Abs(u - upper), Math.Abs(s - surface)));
        return new(l, u, s, interval.UpperIsFreeSurface, interval.LowerBoundaryId, interval.UpperBoundaryId);
    }
}
