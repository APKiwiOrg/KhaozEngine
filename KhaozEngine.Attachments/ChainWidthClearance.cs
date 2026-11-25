using System;
using System.Numerics;
using static KhaozEngine.Attachments.ChainCapsuleCollision;

namespace KhaozEngine.Attachments;

internal static class ChainWidthClearance
{
    private const float PositionTolerance = 0.000001f;

    internal static bool DoesNotWorsen(ChainParticles particles, ReadOnlySpan<ColliderCapsule> obstacles,
        Vector3 parent, Vector3 before, Vector3 after, float chainRadius, double tolerance = PositionTolerance)
    {
        if (before == after)
            return true;

        var root = new Offset(parent);
        var initial = new ChainSegmentContact.Segment(root, new Offset(before) - root);
        var candidate = new ChainSegmentContact.Segment(root, new Offset(after) - root);
        var reach = ChainPlacementCull.SegmentReach(candidate, default, chainRadius, includeOpening: false);
        for (int index = 0; index < Math.Min(obstacles.Length, particles.SolidCapsuleCount); index++)
        {
            double radius = (double)chainRadius + obstacles[index].Radius;
            ref readonly var axis = ref particles.CapsuleAxes[index];
            if (ChainPlacementCull.AxisCullEnabled && !particles.PlacementCull.ReachesAxis(index, reach, swept: false))
                continue;

            bool parentInside = axis.Residual(root, default, out _).LengthSquared < radius * radius;
            double corrected = Distance(axis, candidate, parentInside);
            if (corrected < radius && corrected + tolerance < Distance(axis, initial, parentInside))
                return false;
        }

        return true;
    }

    internal static Vector3 Clip(ChainParticles particles, ReadOnlySpan<ColliderCapsule> obstacles,
        Vector3 parent, Vector3 before, Vector3 after, float length, float radius)
    {
        const double rounding = 0.0000001;
        if (DoesNotWorsen(particles, obstacles, parent, before, after, radius, rounding))
            return after;

        // Progress to the solid boundary on the length sphere. Rejecting a
        // whole proposal discontinuously can shake the free neighbouring panel.
        var initial = before - parent;
        var target = after - parent;
        var fallback = ChainMath.Direction(initial, Vector3.UnitY);
        // A supported corner does not need a new float endpoint for motion
        // below the width solve's own positional precision.
        float low = MathF.Min(1, PositionTolerance / Vector3.Distance(before, after));
        float high = 1;
        var best = At(low);
        if (!DoesNotWorsen(particles, obstacles, parent, before, best, radius, rounding))
            return before;

        for (int attempt = 0; attempt < 20; attempt++)
        {
            float fraction = (low + high) / 2;
            var candidate = At(fraction);
            if (DoesNotWorsen(particles, obstacles, parent, before, candidate, radius, rounding))
            {
                best = candidate;
                low = fraction;
            }
            else
            {
                high = fraction;
            }
        }

        return Vector3.DistanceSquared(before, best) > PositionTolerance * PositionTolerance ? best : before;

        Vector3 At(float fraction) =>
            parent + length * ChainMath.Direction(Vector3.Lerp(initial, target, fraction), fallback);
    }

    private static double Distance(in Axis axis, in ChainSegmentContact.Segment segment, bool parentInside)
    {
        ChainContactCounts.Record(ChainContactQuery.WidthSafety);
        var residual = parentInside ? axis.Residual(segment.Root, segment.Displacement, out _) :
            axis.ClosestResidual(segment, out _, out _);
        return Math.Sqrt(residual.LengthSquared);
    }
}
