using System;
using System.Numerics;
using static KhaozEngine.Attachments.ChainCapsuleCollision;

namespace KhaozEngine.Attachments;

internal static class ChainSegmentContact
{
    private const int ContactPasses = 8;
    private const double FloatProjectionMargin = 1d / (1 << 21);

    [ThreadStatic]
    private static Vector3? _firstCorrectionDirectionForTests;

    // Counts complete segment-axis queries, including the opening pose and velocity contacts.
    [ThreadStatic]
    internal static long? ClosestPointEvaluationsForTests;

    internal static void SetFirstCorrectionDirectionForTests(Vector3? direction) =>
        _firstCorrectionDirectionForTests = direction;

    internal readonly record struct Segment(Offset Root, Offset Displacement)
    {
        internal Segment(Vector3 root, float length, Vector3 direction)
            : this(new Offset(root), new Offset(direction) * length) { }
    }

    internal static Vector3 Clear(ChainParticles particles, int chain, int parent, Vector3 direction, float length,
        float chainRadius, ChainCapsuleCandidates capsules, out bool replayedAll, out int placementCount)
    {
        var start = particles.Positions[parent];
        bool fitsReach = ChainCapsuleCull.FitsReach(direction);
        var placement = fitsReach ? particles.PlacementCull.Select(chain, start, length, chainRadius, capsules) : capsules.All;
        replayedAll = !fitsReach && capsules.IsFiltered;
        placementCount = placement.Count;
        if (placementCount == 0)
            return direction;

        var opening = new ChainOpeningContact(particles, parent);
        var initial = direction;
        direction = ClearPasses(start, direction, length, chainRadius, placement, opening, out bool outsideReach);
        if (!outsideReach)
            return direction;

        replayedAll = true;
        var all = capsules.All;
        placementCount = all.Count;
        return ClearPasses(start, initial, length, chainRadius, all, opening, out _);
    }

    private static Vector3 ClearPasses(Vector3 start, Vector3 direction, float length, float chainRadius,
        ChainCapsuleCandidates capsules, ChainOpeningContact opening, out bool outsideReach)
    {
        var segment = new Segment(start, length, direction);
        var coordinates = Vector3.Abs(start);
        double positionScale = Math.Max(coordinates.X, Math.Max(coordinates.Y, coordinates.Z)) + (double)length;
        var firstCorrectionForTests = _firstCorrectionDirectionForTests;
        Axis scratch = default;
        outsideReach = false;
        for (int pass = 0; pass < ContactPasses; pass++)
        {
            bool changed = false;
            for (int index = 0; index < capsules.Count; index++)
            {
                ref readonly var axis = ref capsules.ReadAxis(index, ref scratch);
                double radius = (double)capsules[index].Radius + chainRadius;
                var residual = axis.ClosestResidual(segment, out double fraction, out _);
                double squared = residual.LengthSquared;
                double radiusSquared = radius * radius;
                bool crossed = opening.Crossed(capsules.OriginalIndex(index), residual, radiusSquared, out var side);
                if (squared >= radiusSquared && !crossed)
                    continue;

                var center = segment.Displacement * fraction - residual;
                bool parentInside = axis.Residual(segment.Root, default, out _).LengthSquared < radiusSquared;
                if (parentInside)
                {
                    // The parent cannot move off the capsule. Clear only the end, as with point contact.
                    var tipResidual = axis.Residual(segment.Root, segment.Displacement, out _);
                    if (tipResidual.LengthSquared >= radiusSquared)
                        continue;
                    center = segment.Displacement - tipResidual;
                    crossed = false;
                }
                else if (crossed && squared >= radiusSquared && center.LengthSquared >= Square(length + radius))
                {
                    // The capsule crossed the segment but has moved beyond its sphere of reach.
                    continue;
                }

                var corrected = firstCorrectionForTests ??
                    ClearSphere(direction, length, center, radius, crossed ? side : direction, useCone: !parentInside,
                        positionScale);
                firstCorrectionForTests = null;
                if (capsules.IsFiltered && !ChainCapsuleCull.FitsReach(corrected))
                {
                    outsideReach = true;
                    return corrected;
                }

                changed |= corrected != direction;
                direction = corrected;
                segment = segment with { Displacement = new Offset(direction) * length };
            }

            if (!changed)
                break;
        }

        return direction;
    }

    private static Vector3 ClearSphere(Vector3 direction, float length, Offset center, double radius,
        Vector3 side, bool useCone, double positionScale)
    {
        double distance = Math.Sqrt(center.LengthSquared);
        if (distance == 0)
            return direction;

        var axis = (center * (1 / distance)).ToVector3();
        // Leave room for the float direction and end position to round. In particular,
        // an endpoint on a capsule must remain outside when it becomes the next parent.
        double margin = Math.Min(FloatProjectionMargin * (positionScale + radius), TouchTolerance / 8);
        double projectedRadius = useCone ? Math.Min(radius + margin, distance) : radius + margin;
        double tangentSquared = (distance - projectedRadius) * (distance + projectedRadius);
        double boundary = useCone && Square(length) >= tangentSquared
            ? Math.Sqrt(Math.Max(0, tangentSquared)) / distance
            : (Square(length) + tangentSquared) / (2 * length * distance);
        double cosine = Math.Clamp(boundary, -1, 1);
        double sine = Math.Sqrt(1 - cosine * cosine);
        var tangent = ChainMath.Tangent(side, axis);
        if (tangent == Vector3.Zero)
            tangent = ChainMath.Perpendicular(axis);
        return (float)cosine * axis + (float)sine * tangent;
    }

    private static double Square(double value) => value * value;

    internal static bool TryContact(in Segment segment, float chainRadius, in ColliderCapsule capsule, in Axis axis,
        out Vector3 normal, out double fraction, out double along)
    {
        var residual = axis.ClosestResidual(segment, out fraction, out along);
        double squared = residual.LengthSquared;
        double radius = capsule.Radius + (double)chainRadius + TouchTolerance;
        if (squared == 0 || squared > radius * radius)
        {
            normal = Vector3.Zero;
            return false;
        }

        normal = (residual * (1 / Math.Sqrt(squared))).ToVector3();
        return true;
    }
}
