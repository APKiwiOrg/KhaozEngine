using System;
using System.Numerics;
using static KhaozEngine.Attachments.ChainCapsuleCollision;

namespace KhaozEngine.Attachments;

internal static class ChainContactProjection
{
    private const int RoundingRefinements = 4;

    internal readonly record struct AxisSample(Offset Displacement, Offset Residual, double Fraction);

    internal static Vector3 Clear(Vector3 start, float length, Vector3 direction, Offset center, double radius,
        Vector3 side, bool parentInside, in Axis capsule, double along,
        ChainPlacementCull cull, int obstacle, float chainRadius, AxisSample last)
    {
        double distance = Math.Sqrt(center.LengthSquared);
        if (distance == 0)
            return direction;

        var axis = center * (1 / distance);
        var floatAxis = axis.ToVector3();
        var across = ChainMath.Tangent(side, floatAxis);
        if (across == Vector3.Zero)
            across = ChainMath.Perpendicular(floatAxis);
        var tangent = Offset.Cross(axis, Offset.Cross(new Offset(across), axis));
        tangent *= 1 / Math.Sqrt(tangent.LengthSquared);

        double projectedRadius = radius;
        double slack = 1e-15 * (radius + distance);
        bool checkMargin = ChainContactOptimizations.Use(ChainContactOptimization.RoundingMargin);
        bool axisCull = ChainPlacementCull.AxisCullEnabled;
        bool reuse = ChainContactOptimizations.Use(ChainContactOptimization.RoundingReuse);
        for (int refinement = 0; ; refinement++)
        {
            var projected = Boundary(length, distance, projectedRadius, axis, tangent, useCone: !parentInside);
            direction = projected.ToVector3();

            if (checkMargin && refinement == RoundingRefinements)
                return direction;

            var stored = ChainSegmentContact.Segment.FromStoredEndpoint(start, length, direction);
            var projectedOffset = projected * length;
            double fraction;
            Offset residual;
            if (reuse && last.Displacement.SameBits(stored.Displacement))
            {
                // The previous exact query already tested these stored bits.
                residual = last.Residual;
                fraction = last.Fraction;
            }
            else
            {
                var boundSegment = parentInside ? new ChainSegmentContact.Segment(stored.Root + stored.Displacement, default) : stored;
                if (axisCull && !cull.ReachesAxis(obstacle,
                    ChainPlacementCull.SegmentReach(boundSegment, default, chainRadius + (float)TouchTolerance,
                        includeOpening: false), swept: false))
                    return direction;
                if (checkMargin && !ChainRoundingMargin.NeedsRecheck(stored, projectedOffset, capsule, along, radius, slack, parentInside))
                    return direction;

                fraction = 1;
                if (!parentInside)
                    ChainContactCounts.Record(ChainContactQuery.RoundingRecheck);
                residual = parentInside ? capsule.Residual(stored.Root, stored.Displacement, out _) :
                    capsule.ClosestResidual(stored, out fraction, out _);
                if (reuse)
                    last = new AxisSample(stored.Displacement, residual, fraction);
            }
            double squared = residual.LengthSquared;
            if (squared >= radius * radius || refinement == RoundingRefinements)
                return direction;

            // The error in the stored child contributes only its share at the closest point.
            // A larger deficit means the closest point on the capsule has moved. The next
            // ordinary contact pass can then choose a different local sphere.
            var rounding = (stored.Displacement - projectedOffset) * fraction;
            double error = Math.Sqrt(rounding.LengthSquared);
            double deficit = radius - Math.Sqrt(squared);
            if (deficit > error + slack)
                return direction;

            double expanded = projectedRadius + Math.Max(deficit, error) + slack;
            if (!parentInside)
                expanded = Math.Min(expanded, distance);
            if (expanded <= projectedRadius)
                return direction;
            projectedRadius = expanded;
        }
    }

    internal static double BoundaryCosine(double length, double distance, double radius, bool useCone)
    {
        double tangentSquared = (distance - radius) * (distance + radius);
        double lengthSquared = length * length;
        return useCone && lengthSquared >= tangentSquared ? Math.Sqrt(Math.Max(0, tangentSquared)) / distance :
            Math.Clamp((lengthSquared + tangentSquared) / (2 * length * distance), -1, 1);
    }

    private static Offset Boundary(double length, double distance, double radius, Offset axis, Offset tangent, bool useCone)
    {
        double tangentSquared = (distance - radius) * (distance + radius);
        double lengthSquared = length * length;
        if (useCone && lengthSquared >= tangentSquared)
        {
            double cosine = Math.Sqrt(Math.Max(0, tangentSquared)) / distance;
            return axis * cosine + tangent * (radius / distance);
        }

        double tipCosine = BoundaryCosine(length, distance, radius, useCone: false);
        double tipSine = Math.Sqrt((1 - tipCosine) * (1 + tipCosine));
        return axis * tipCosine + tangent * tipSine;
    }
}
