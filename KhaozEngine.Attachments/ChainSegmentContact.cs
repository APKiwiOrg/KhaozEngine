using System;
using System.Numerics;
using static KhaozEngine.Attachments.ChainCapsuleCollision;

namespace KhaozEngine.Attachments;

internal static class ChainSegmentContact
{
    internal const int ContactPasses = 8;
    private const double NearParentFraction = 0.125;
    private static readonly float NearParentTurnCosine = MathF.Cos(0.16f);

    [ThreadStatic]
    private static Vector3? _firstCorrectionDirectionForTests;

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
        float chainRadius, ChainCapsuleCandidates capsules, out bool replayedAll, out int placementCount,
        int chain, Vector3 rest, float bendCosine)
    {
        var start = particles.Positions[parent];
        bool fitsReach = ChainCapsuleCull.FitsReach(direction);
        var placement = fitsReach ? particles.PlacementCull.Select(placementRow, start, length, chainRadius, capsules) : capsules.All;
        replayedAll = !fitsReach && capsules.IsFiltered;
        placementCount = placement.Count;
        if (placementCount == 0)
            return direction;

        var opening = new ChainOpeningContact(particles, chain, parent, capsules.ObstacleCount);
        var initial = direction;
        direction = ClearPasses(start, direction, length, chainRadius, placement, opening, particles.PlacementCull,
            particles.SolidCapsuleCount, rest, bendCosine, out bool outsideReach);
        if (!outsideReach)
            return direction;

        replayedAll = true;
        var all = capsules.All;
        placementCount = all.Count;
        return ClearPasses(start, initial, length, chainRadius, all, opening, particles.PlacementCull,
            particles.SolidCapsuleCount, rest, bendCosine, out _);
    }

    private static Vector3 ClearPasses(Vector3 start, Vector3 direction, float length, float chainRadius,
        ChainCapsuleCandidates capsules, ChainOpeningContact opening, ChainPlacementCull cull,
        int solidCapsules, Vector3 rest, float bendCosine, out bool outsideReach)
    {
        var segment = Segment.FromStoredEndpoint(start, length, direction);
        var firstCorrectionForTests = _firstCorrectionDirectionForTests;
        Axis scratch = default;
        outsideReach = false;
        bool tightCull = ChainContactOptimizations.Use(ChainContactOptimization.SegmentCull) && !ChainPlacementCull.DisableForTests;
        bool axisCull = ChainPlacementCull.AxisCullEnabled;
        bool useTolerance = ChainContactOptimizations.Use(ChainContactOptimization.PassTolerance);
        float contactReach = chainRadius + (float)TouchTolerance;
        var parentBound = new ChainPlacementCull.SweptSphere(start, contactReach);
        var tightBound = default(ChainPlacementCull.SweptSphere);
        var counts = ChainContactCounts.Active;
        var initial = direction;
        Span<ChainSegmentAlternatives.Support> supports = stackalloc ChainSegmentAlternatives.Support[4];
        int supportCount = 0;
        bool solidContact = false, competing = false;
        int passes = 0;
        for (int pass = 0; pass < ContactPasses; pass++)
        {
            passes++;
            bool useTight = tightCull && pass > 0;
            if (useTight || axisCull)
                tightBound = ChainPlacementCull.SegmentReach(segment, opening.Segment, contactReach, includeOpening: true, opening.Prediction);

            bool changed = false;
            for (int index = 0; index < capsules.Count; index++)
            {
                int obstacle = capsules.OriginalIndex(index);
                if ((useTight && !cull.ReachesTight(obstacle, tightBound)) ||
                    (axisCull && !cull.ReachesAxis(obstacle, tightBound, swept: true)))
                    continue;

                ref readonly var axis = ref capsules.ReadAxis(index, ref scratch);
                double radius = (double)capsules[index].Radius + chainRadius;
                ChainContactCounts.Record(ChainContactQuery.SegmentPlacement);
                var residual = axis.ClosestResidual(segment, out double fraction, out double along);
                double squared = residual.LengthSquared;
                double radiusSquared = radius * radius;
                var side = opening.Side(obstacle, axis, radius, chainRadius);
                bool crossed = side != Vector3.Zero && (squared < radiusSquared || Offset.Dot(new Offset(side), residual) < 0);
                if (squared >= radiusSquared && !crossed)
                    continue;

                var center = segment.Displacement * fraction - residual;
                bool parentInside = (!axisCull || cull.ReachesAxis(obstacle, parentBound, swept: false)) &&
                    axis.Residual(segment.Root, default, out _).LengthSquared < radiusSquared;
                if (parentInside)
                {
                    // The parent cannot move off the capsule. Clear only the end, as with point contact.
                    var endpoint = new ChainPlacementCull.SweptSphere((segment.Root + segment.Displacement).ToVector3(), contactReach);
                    if (axisCull && !cull.ReachesAxis(obstacle, endpoint, swept: false))
                        continue;
                    var tipResidual = axis.Residual(segment.Root, segment.Displacement, out along);
                    if (tipResidual.LengthSquared >= radiusSquared)
                        continue;
                    center = segment.Displacement - tipResidual;
                    residual = tipResidual;
                    fraction = 1;
                    crossed = false;
                }
                else if (crossed && squared >= radiusSquared && center.LengthSquared >= Square(length + radius))
                {
                    // The capsule crossed the segment but has moved beyond its sphere of reach.
                    continue;
                }

                bool forced = firstCorrectionForTests.HasValue;
                var corrected = firstCorrectionForTests ??
                    ChainContactProjection.Clear(start, length, direction, center, radius,
                        crossed ? side : direction, parentInside, axis, along, cull, obstacle, chainRadius,
                        new ChainContactProjection.AxisSample(segment.Displacement, residual, fraction));
                firstCorrectionForTests = null;
                if (obstacle >= solidCapsules && fraction <= NearParentFraction)
                {
                    // A contact close to the parent has little authority over
                    // the rest of the chain. The bend wall keeps its priority.
                    if (Vector3.Dot(direction, rest) < bendCosine)
                        continue;
                    corrected = ChainConstraints.ClampBend(corrected, rest, bendCosine);
                    if (solidContact || !ChainSegmentAlternatives.PreservesSolids(start, length, chainRadius,
                        direction, corrected, solidCapsules, capsules, opening))
                    {
                        corrected = ChainConstraints.ClampBend(corrected, initial, NearParentTurnCosine);
                        if (!ChainSegmentAlternatives.PreservesSolids(start, length, chainRadius, direction, corrected,
                            solidCapsules, capsules, opening))
                            continue;
                    }
                }
                else if (!forced && obstacle < solidCapsules)
                {
                    solidContact = true;
                    var turn = corrected - direction;
                    double tolerance = ChainContactPass.Tolerance / length;
                    if (turn.LengthSquared() > tolerance * tolerance)
                    {
                        bool known = false;
                        foreach (ref readonly var support in supports[..supportCount])
                        {
                            known |= support.Obstacle == obstacle;
                            competing |= support.Obstacle != obstacle && Vector3.Dot(support.Turn, turn) < 0;
                        }
                        if (!known && supportCount < supports.Length)
                            supports[supportCount++] = ChainSegmentAlternatives.Support.From(obstacle, center, radius,
                                length, parentInside, turn);
                    }
                }
                if (capsules.IsFiltered && !ChainCapsuleCull.FitsReach(corrected))
                {
                    outsideReach = true;
                    counts?.RecordSegmentPasses(passes);
                    return corrected;
                }

                var nextSegment = Segment.FromStoredEndpoint(start, length, corrected);
                changed |= useTolerance ? ChainContactPass.Moved(segment.Displacement, nextSegment.Displacement) :
                    corrected != direction;
                direction = corrected;
                segment = nextSegment;
                if (useTight || axisCull)
                    tightBound = ChainPlacementCull.SegmentReach(segment, opening.Segment, contactReach, includeOpening: true, opening.Prediction);
            }

            if (!changed)
                break;
        }

        counts?.RecordSegmentPasses(passes);
        return competing ? ChainSegmentAlternatives.Choose(start, length, chainRadius, initial, direction,
            solidCapsules, capsules, opening, supports[..supportCount]) : direction;
    }

    private static double Square(double value) => value * value;

    internal static bool TryContact(in Segment segment, float chainRadius, in ColliderCapsule capsule, in Axis axis,
        out Vector3 normal, out double fraction, out double along)
    {
        ChainContactCounts.Record(ChainContactQuery.SegmentVelocity);
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
