using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>A conservative precision proof, never a replacement tolerance. Empty geometry does not exempt a
/// movement query from stored-pose and capsule-conversion refusals.</summary>
internal static class FlatRangeRoundoff
{
    internal static bool Fits(Vector3 minimum, Vector3 maximum, Vector3 origin, in MoveTuning tuning)
    {
        // Bound complete float spacings, rather than half-ulp rounding. The flat route's longest dependent
        // expression has fewer than sixteen rounded operations across frame translation, shell centre/lift,
        // direction-distance reconstruction, stored endpoint and capsule conversion. Charging sixteen of
        // every operand's worst spacing encloses those paths and their binary64 residual bounds. If this
        // coarse bound cannot fit the unchanged skin, the original solver decides each trial's precision.
        double spacings = Spacing(MathF.Max(MathF.Abs(minimum.X), MathF.Abs(maximum.X))) +
            Spacing(MathF.Max(MathF.Abs(minimum.Y), MathF.Abs(maximum.Y))) +
            Spacing(MathF.Max(MathF.Abs(minimum.Z), MathF.Abs(maximum.Z))) +
            Spacing(origin.X) + Spacing(origin.Y) + Spacing(origin.Z) +
            Spacing(tuning.CapsuleRadius) + Spacing(tuning.CapsuleHalfHeight) +
            Spacing(tuning.StepHeight) + Spacing(tuning.FootRadiusFraction * tuning.CapsuleRadius);
        return double.IsFinite(spacings) && Math.BitIncrement(16d * Math.BitIncrement(spacings)) <=
            MovementQueryLease.CoverageSkinMetres;
    }

    static double Spacing(float value)
    {
        value = MathF.Abs(value);
        return (double)MathF.BitIncrement(value) - value;
    }
}
