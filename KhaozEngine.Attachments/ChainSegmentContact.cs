using System;
using System.Numerics;
using static KhaozEngine.Attachments.ChainCapsuleCollision;

namespace KhaozEngine.Attachments;

internal static class ChainSegmentContact
{
    private const int ContactPasses = 8;

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

        internal static Segment FromStoredEndpoint(Vector3 start, float length, Vector3 direction)
        {
            var root = new Offset(start);
            return new Segment(root, new Offset(start + length * direction) - root);
        }
    }

    internal static Vector3 Clear(ChainParticles particles, int placementRow, int parent, Vector3 direction, float length,
        float chainRadius, ChainCapsuleCandidates capsules, out bool replayedAll, out int placementCount)
    {
        var start = particles.Positions[parent];
        bool fitsReach = ChainCapsuleCull.FitsReach(direction);
        var placement = fitsReach ? particles.PlacementCull.Select(placementRow, start, length, chainRadius, capsules) : capsules.All;
        replayedAll = !fitsReach && capsules.IsFiltered;
        placementCount = placement.Count;
        if (placementCount == 0)
            return direction;

        var opening = new ChainOpeningContact(particles, parent, capsules.ObstacleCount);
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
        var segment = Segment.FromStoredEndpoint(start, length, direction);
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
                    ChainContactProjection.Clear(start, length, direction, center, radius,
                        crossed ? side : direction, parentInside, axis);
                firstCorrectionForTests = null;
                if (capsules.IsFiltered && !ChainCapsuleCull.FitsReach(corrected))
                {
                    outsideReach = true;
                    return corrected;
                }

                changed |= corrected != direction;
                direction = corrected;
                segment = Segment.FromStoredEndpoint(start, length, direction);
            }

            if (!changed)
                break;
        }

        return direction;
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
