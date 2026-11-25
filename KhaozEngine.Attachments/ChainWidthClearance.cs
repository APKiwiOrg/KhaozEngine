using System;
using System.Numerics;
using static KhaozEngine.Attachments.ChainCapsuleCollision;

namespace KhaozEngine.Attachments;

internal static class ChainWidthClearance
{
    internal const float PositionTolerance = 0.000001f;

    internal static Vector3 Clip(ChainParticles particles, ReadOnlySpan<ColliderCapsule> obstacles,
        Vector3 parent, Vector3 before, Vector3 after, float length, float radius, ChainWidthNearSolids? nearSolids, int chain)
    {
        int solids = Math.Min(obstacles.Length, particles.SolidCapsuleCount);
        if (solids == 0 || before == after)
            return after;
        if (length <= 0 || before == parent || after == parent)
            return before;

        const double rounding = 0.0000001;
        var arc = new ChainWidthArc(parent, before, after, length);
        if (arc.Angle == 0)
            return before;

        var root = new Offset(parent);
        var proposed = new ChainSegmentContact.Segment(root, new Offset(after) - root);
        var solidObstacles = obstacles[..solids];
        var candidates = nearSolids is null ? new ChainWidthNearSolids.Candidates(solids) :
            nearSolids.Select(particles, solidObstacles, chain, parent, length, radius);
        int count = candidates.Count;
        if (count == 0 && candidates.ForStored(proposed.Displacement).Count == 0)
            return after;

        var initial = new ChainSegmentContact.Segment(root, new Offset(before) - root);
        // Enclose the whole swept sector by padding its chord hull with the
        // maximum arc bulge and the stored endpoints' length rounding.
        double sag = Math.Sin(arc.Angle / 4);
        double lengthError = Math.Max(Math.Abs(Math.Sqrt(initial.Displacement.LengthSquared) - length),
            Math.Abs(Math.Sqrt(proposed.Displacement.LengthSquared) - length));
        float padding = (float)(2 * length * sag * sag + lengthError) + PositionTolerance;
        var sweptReach = ChainPlacementCull.SegmentReach(proposed, initial, radius + padding, includeOpening: true);
        var pointReach = new ChainPlacementCull.SweptSphere(parent, length + radius + PositionTolerance);
        bool axisCull = ChainPlacementCull.AxisCullEnabled;
        var counts = ChainContactCounts.Active;
        double accepted = arc.Angle;
        for (int row = 0; row < count; row++)
        {
            int index = candidates.Index(row);
            ref readonly var obstacle = ref solidObstacles[index];
            ref readonly var axis = ref particles.CapsuleAxes[index];
            var reach = axis.IsPoint ? pointReach : sweptReach;
            if (axisCull && !particles.PlacementCull.ReachesAxis(index, reach, swept: false))
                continue;

            // An irrelevant finite axis must not consume an interval search budget.
            // Apply this rejection even without the optional float cull.
            if (!axis.IsPoint && BeyondReach(reach.Center, reach.Radius, obstacle, axis))
                continue;

            double inflated = (double)radius + obstacle.Radius;
            double parentDistance = candidates.ParentDistanceSquared(row, axis, root);
            double ballReach = length + inflated + PositionTolerance;
            if (parentDistance > ballReach * ballReach)
                continue;

            bool tipOnly = parentDistance < inflated * inflated;
            var residual = Residual(axis, initial, tipOnly, counts);
            double required = Math.Min(inflated, Math.Sqrt(residual.LengthSquared)) - 0.5 * rounding;
            if (required > 0)
                accepted = arc.ClearPrefix(axis, residual, tipOnly, required, accepted, counts);
            if (accepted * length <= rounding)
                return before;
        }
        var candidate = accepted == arc.Angle ? after : arc.Endpoint(accepted);
        if (StoredClearanceHolds(particles, solidObstacles, candidates, initial, candidate, radius, rounding, axisCull, counts))
            return accepted == arc.Angle || Moved(before, candidate) ? candidate : before;

        // Float storage can put the endpoint just past the certified limit.
        // Retreat within the already certified prefix, where no hidden collision
        // can separate two accepted samples.
        double low = 0, high = accepted;
        var best = before;
        for (int i = 0; i < 20; i++)
        {
            double middle = (low + high) * 0.5;
            candidate = arc.Endpoint(middle);
            if (StoredClearanceHolds(particles, solidObstacles, candidates, initial, candidate, radius, rounding, axisCull, counts))
            {
                best = candidate;
                low = middle;
            }
            else
            {
                high = middle;
            }
        }
        return Moved(before, best) ? best : before;
    }

    private static bool Moved(Vector3 before, Vector3 after) =>
        Vector3.DistanceSquared(before, after) > PositionTolerance * PositionTolerance;

    private static bool StoredClearanceHolds(ChainParticles particles, ReadOnlySpan<ColliderCapsule> solids,
        in ChainWidthNearSolids.Candidates candidates, in ChainSegmentContact.Segment initial, Vector3 candidate,
        float radius, double rounding, bool axisCull, ChainContactCounts? counts)
    {
        var root = initial.Root;
        var stored = new ChainSegmentContact.Segment(root, new Offset(candidate) - root);
        // A rounded or authored endpoint can fall beyond the nominal length sphere.
        var eligible = candidates.ForStored(stored.Displacement);
        int count = eligible.Count;
        if (count == 0)
            return true;
        var reach = ChainPlacementCull.SegmentReach(stored, default, radius, includeOpening: false);
        for (int row = 0; row < count; row++)
        {
            int index = eligible.Index(row);
            if (axisCull && !particles.PlacementCull.ReachesAxis(index, reach, swept: false))
                continue;

            double inflated = (double)radius + solids[index].Radius;
            ref readonly var axis = ref particles.CapsuleAxes[index];
            bool tipOnly = eligible.ParentDistanceSquared(row, axis, root) < inflated * inflated;
            double distance = Distance(axis, stored, tipOnly, counts);
            if (distance < inflated && distance + rounding < Distance(axis, initial, tipOnly, counts))
                return false;
        }
        return true;
    }

    private static double Distance(in Axis axis, in ChainSegmentContact.Segment segment, bool parentInside,
        ChainContactCounts? counts) =>
        Math.Sqrt(Residual(axis, segment, parentInside, counts).LengthSquared);

    private static Offset Residual(in Axis axis, in ChainSegmentContact.Segment segment, bool parentInside,
        ChainContactCounts? counts)
    {
        counts?.Record(ChainContactQuery.WidthSafety);
        return parentInside ? axis.Residual(segment.Root, segment.Displacement, out _) :
            axis.ClosestResidual(segment, out _, out _);
    }
}
