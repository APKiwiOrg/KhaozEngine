using System;

namespace KhaozEngine.Tests.Gpu;

/// <summary>F0 regression envelope, not a geometric certificate or a universal error proof.</summary>
internal static class CoverageCarryRounding
{
    internal const double FloatEpsilon = 1.0 / 8388608;

    internal static void Require(CoverageCarryError error)
    {
        if (!Within(error.Position, 32 * FloatEpsilon) || !Within(error.Normal, 256 * FloatEpsilon)
            || !Within(error.Tangent, 128 * FloatEpsilon) || error.TangentHandednessAbsolute != 0)
            throw new InvalidOperationException($"Actor {error.Actor} exceeds the F0 native-float rounding envelope. "
                + $"Position {error.Position.MaximumRelative:R}, normal {error.Normal.MaximumRelative:R}, tangent {error.Tangent.MaximumRelative:R}.");
    }

    static bool Within(CoverageCarryError.VectorError error, double bound)
        => error.ZeroReferenceNonzeroErrors == 0 && error.MaximumRelative <= bound;
}
