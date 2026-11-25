using System;
using static KhaozEngine.Attachments.ChainCapsuleCollision;

namespace KhaozEngine.Attachments;

internal static class ChainRoundingMargin
{
    internal static bool NeedsRecheck(in ChainSegmentContact.Segment stored, Offset projected, in Axis capsule,
        double along, double radius, double slack, bool tipOnly)
    {
        // The stored displacement is at most this far from the analytic segment.
        // The old refinement compares its deficit with at most the same error.
        var delta = stored.Displacement - projected;
        double rounding = MagnitudeBound(delta);
        double arithmetic = 1e-9 * (MagnitudeBound(stored.Root) + MagnitudeBound(capsule.Start) +
            MagnitudeBound(capsule.End) + MagnitudeBound(projected) + radius);

        along = Math.Clamp(along, 0, 1);
        var witness = (capsule.Start - stored.Root) * (1 - along) + (capsule.End - stored.Root) * along;
        double lengthSquared = projected.LengthSquared;
        double fraction = tipOnly ? 1 : lengthSquared == 0 ? 0 :
            Math.Clamp(Offset.Dot(witness, projected) / lengthSquared, 0, 1);
        double squared = (projected * fraction - witness).LengthSquared;

        if (capsule.IsPoint)
        {
            double outside = radius + rounding + arithmetic;
            if (squared > outside * outside)
                return false;
        }
        else if (!tipOnly)
        {
            // The witness only provides an upper bound for a non-point axis.
            // Leave nearly parallel projections to the exact feature query.
            var direction = capsule.Direction;
            if (Offset.Cross(projected, direction).LengthSquared <= 1e-10 * lengthSquared * direction.LengthSquared)
                return true;
        }

        double inside = radius - 2 * rounding - arithmetic - slack;
        return !(inside > 0 && squared < inside * inside);
    }

    private static double MagnitudeBound(Offset value) => Math.Abs(value.X) + Math.Abs(value.Y) + Math.Abs(value.Z);
}
