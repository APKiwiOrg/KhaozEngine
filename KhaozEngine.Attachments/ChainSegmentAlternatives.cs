using System;
using System.Numerics;
using static KhaozEngine.Attachments.ChainCapsuleCollision;
using static KhaozEngine.Attachments.ChainSegmentContact;

namespace KhaozEngine.Attachments;

// Adjacent projections can oppose each other. Reuse their supporting planes
// on the unit direction sphere and check the resulting choices against the
// actual capsules, including those that did not supply a plane.
internal static class ChainSegmentAlternatives
{
    private const double Settlement = ChainContactPass.Tolerance;
    private static readonly float ResidualTurnCosine = MathF.Cos(0.25f);

    internal readonly record struct Support(int Obstacle, Offset Axis, double Cosine, Vector3 Turn)
    {
        internal static Support From(int obstacle, Offset center, double radius, float length, bool parentInside, Vector3 turn)
        {
            double distance = Math.Sqrt(center.LengthSquared);
            if (distance == 0)
                return new(obstacle, default, 1, turn);

            return new(obstacle, center * (1 / distance),
                ChainContactProjection.BoundaryCosine(length, distance, radius, useCone: !parentInside), turn);
        }
    }

    internal static Vector3 Choose(Vector3 start, float length, float chainRadius, Vector3 initial,
        Vector3 sequential, int solids, in ChainCapsuleCandidates capsules, in ChainOpeningContact opening,
        ReadOnlySpan<Support> supports)
    {
        if (supports.Length < 2)
            return sequential;

        var selected = initial;
        var best = Measure(initial, start, length, chainRadius, solids, capsules, in opening);
        Offer(sequential, start, length, chainRadius, initial, solids, capsules, in opening, ref selected, ref best);
        for (int i = 0; i < supports.Length; i++)
        {
            for (int j = 0; j < i; j++)
            {
                if (!Intersect(supports[j], supports[i], out var a, out var b))
                    continue;
                Offer(a, start, length, chainRadius, initial, solids, capsules, in opening, ref selected, ref best);
                Offer(b, start, length, chainRadius, initial, solids, capsules, in opening, ref selected, ref best);
            }
        }
        return selected;
    }

    private static bool Intersect(in Support first, in Support second, out Vector3 a, out Vector3 b)
    {
        a = b = default;
        var cross = Offset.Cross(first.Axis, second.Axis);
        double squared = cross.LengthSquared;
        if (squared <= 1e-12)
            return false;

        double dot = Offset.Dot(first.Axis, second.Axis);
        var closest = (first.Axis * (first.Cosine - dot * second.Cosine) +
            second.Axis * (second.Cosine - dot * first.Cosine)) * (1 / squared);
        double remaining = 1 - closest.LengthSquared;
        if (remaining < 0)
            return false;

        var axis = cross * Math.Sqrt(remaining / squared);
        a = (closest + axis).ToVector3();
        b = (closest - axis).ToVector3();
        return true;
    }

    private static void Offer(Vector3 proposal, Vector3 start, float length, float radius, Vector3 initial, int solids,
        in ChainCapsuleCandidates capsules, in ChainOpeningContact opening, ref Vector3 selected, ref Residual best)
    {
        var score = Measure(proposal, start, length, radius, solids, capsules, in opening);
        if (score.Solid > Settlement && Vector3.Dot(proposal, initial) < ResidualTurnCosine)
        {
            // With no clear orientation among the proposals, keep progress
            // local instead of selecting a distant but still embedded turn.
            proposal = ChainConstraints.ClampBend(proposal, initial, ResidualTurnCosine);
            score = Measure(proposal, start, length, radius, solids, capsules, in opening);
        }

        if (!score.IsBetterThan(best, proposal, selected, initial))
            return;

        selected = proposal;
        best = score;
    }

    internal static bool PreservesSolids(Vector3 start, float length, float chainRadius, Vector3 current,
        Vector3 next, int solids, in ChainCapsuleCandidates capsules, in ChainOpeningContact opening)
    {
        var before = Segment.FromStoredEndpoint(start, length, current);
        var after = Segment.FromStoredEndpoint(start, length, next);
        Axis scratch = default;
        for (int index = 0; index < capsules.Count; index++)
        {
            int obstacle = capsules.OriginalIndex(index);
            if (obstacle >= solids)
                break;
            ref readonly var axis = ref capsules.ReadAxis(index, ref scratch);
            double radius = (double)capsules[index].Radius + chainRadius;
            var side = opening.Side(obstacle, axis, radius, chainRadius);
            if (Depth(after, axis, radius, side, opening.Counts) > Depth(before, axis, radius, side, opening.Counts) + Settlement)
                return false;
        }
        return true;
    }

    private static Residual Measure(Vector3 direction, Vector3 start, float length, float chainRadius,
        int solids, in ChainCapsuleCandidates capsules, in ChainOpeningContact opening)
    {
        var segment = Segment.FromStoredEndpoint(start, length, direction);
        Axis scratch = default;
        var result = default(Residual);
        for (int index = 0; index < capsules.Count; index++)
        {
            int obstacle = capsules.OriginalIndex(index);
            ref readonly var axis = ref capsules.ReadAxis(index, ref scratch);
            double radius = (double)capsules[index].Radius + chainRadius;
            var side = opening.Side(obstacle, axis, radius, chainRadius);
            double depth = Depth(segment, axis, radius, side, opening.Counts);
            if (obstacle < solids)
                result.Solid = Math.Max(result.Solid, depth);
            else
                result.Layer = Math.Max(result.Layer, depth);
        }
        return result;
    }

    private static double Depth(in Segment segment, in Axis axis, double radius, Vector3 side, ChainContactCounts? counts)
    {
        counts?.Record(ChainContactQuery.ConflictCheck);
        if (axis.Residual(segment.Root, default, out _).LengthSquared < radius * radius)
            return Math.Max(0, radius - Math.Sqrt(axis.Residual(segment.Root, segment.Displacement, out _).LengthSquared));

        var residual = axis.ClosestResidual(segment, out double fraction, out _);
        double distance = Math.Sqrt(residual.LengthSquared);
        if (side != Vector3.Zero && Offset.Dot(new Offset(side), residual) < 0)
        {
            double reach = Math.Sqrt(segment.Displacement.LengthSquared) + radius;
            if ((segment.Displacement * fraction - residual).LengthSquared < reach * reach)
                return radius + distance;
        }

        return Math.Max(0, radius - distance);
    }

    private struct Residual
    {
        internal double Solid, Layer;

        internal readonly bool IsBetterThan(in Residual other, Vector3 proposal, Vector3 selected, Vector3 initial)
        {
            if (Math.Abs(Solid - other.Solid) > Settlement)
                return Solid < other.Solid;
            if (Math.Abs(Layer - other.Layer) > Settlement)
                return Layer < other.Layer;

            float offeredTravel = Vector3.DistanceSquared(proposal, initial);
            float acceptedTravel = Vector3.DistanceSquared(selected, initial);
            return offeredTravel < acceptedTravel;
        }
    }
}
