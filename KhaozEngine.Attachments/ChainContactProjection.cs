using System;
using System.Numerics;
using static KhaozEngine.Attachments.ChainCapsuleCollision;

namespace KhaozEngine.Attachments;

internal static class ChainContactProjection
{
    private const int RoundingRefinements = 4;

    internal static Vector3 Clear(Vector3 start, float length, Vector3 direction, Offset center, double radius,
        Vector3 side, bool parentInside, in Axis capsule)
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
        for (int refinement = 0; ; refinement++)
        {
            var projected = Boundary(length, distance, projectedRadius, axis, tangent, useCone: !parentInside);
            direction = projected.ToVector3();

            var stored = ChainSegmentContact.Segment.FromStoredEndpoint(start, length, direction);
            double fraction = 1;
            if (!parentInside)
                ChainContactCounts.Record(ChainContactQuery.RoundingRecheck);
            var residual = parentInside
                ? capsule.Residual(stored.Root, stored.Displacement, out _)
                : capsule.ClosestResidual(stored, out fraction, out _);
            double squared = residual.LengthSquared;
            if (squared >= radius * radius || refinement == RoundingRefinements)
                return direction;

            // The error in the stored child contributes only its share at the closest point.
            // A larger deficit means the closest point on the capsule has moved. The next
            // ordinary contact pass can then choose a different local sphere.
            var rounding = (stored.Displacement - projected * length) * fraction;
            double error = Math.Sqrt(rounding.LengthSquared);
            double deficit = radius - Math.Sqrt(squared);
            double slack = 1e-15 * (radius + distance);
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

    private static Offset Boundary(double length, double distance, double radius, Offset axis, Offset tangent, bool useCone)
    {
        double tangentSquared = (distance - radius) * (distance + radius);
        double lengthSquared = length * length;
        if (useCone && lengthSquared >= tangentSquared)
        {
            double cosine = Math.Sqrt(Math.Max(0, tangentSquared)) / distance;
            return axis * cosine + tangent * (radius / distance);
        }

        double tipCosine = Math.Clamp((lengthSquared + tangentSquared) / (2 * length * distance), -1, 1);
        double tipSine = Math.Sqrt((1 - tipCosine) * (1 + tipCosine));
        return axis * tipCosine + tangent * tipSine;
    }
}
