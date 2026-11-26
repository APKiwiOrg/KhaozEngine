using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal sealed class ClothSelfContact(ClothRig rig, int maxVisits)
{
    private const float ContactMargin = .00025f;
    private const float SolveTolerance = .00025f;
    private const float VelocityTouchRoundoff = .000001f;

    private readonly ClothSelfBroadPhase _broadPhase = new(rig, maxVisits);
    private readonly float _thickness = 2 * rig.Material.HalfThickness;

    internal ClothSelfBroadPhase BroadPhase => _broadPhase;

    internal void Begin() => _broadPhase.Begin();

    internal bool Prepare(Vector3[] opening, Vector3[] positions, ClothContactWorkspace work) =>
        _broadPhase.EnsureCoverage(opening, positions, .5f * (_thickness + ContactMargin + VelocityTouchRoundoff), work);

    internal ClothStepStatus Visit(Vector3[] opening, Vector3[] positions, Vector3[] bias,
        ClothContactWorkspace work, ClothContactVelocity velocities, bool verify, bool collectVelocity)
    {
        float reach = _thickness + ContactMargin + VelocityTouchRoundoff;
        foreach (ClothSelfBroadPhase.Pair candidate in _broadPhase.Pairs)
        {
            if (!work.Visit()) return ClothStepStatus.ContactWork;
            ClothTriangle a = rig.Triangles[candidate.First], b = rig.Triangles[candidate.Last];
            var beforeA = new ClothFacet(a, opening);
            var afterA = new ClothFacet(a, positions);
            var beforeB = new ClothFacet(b, opening);
            var afterB = new ClothFacet(b, positions);
            if (!ClothBounds.Enclose(beforeA.Bounds, afterA.Bounds).Overlaps(ClothBounds.Enclose(beforeB.Bounds, afterB.Bounds).Expand(reach)))
                continue;
            var pair = new ClothSelfSweep(beforeA, afterA, beforeB, afterB);
            if (pair.IsOutsideReach(reach)) continue;
            if (!work.Candidate()) return ClothStepStatus.ContactCapacity;

            ClothFacetContact hit = ClothFacetContact.Closest(afterA, afterB);
            float distance = (float)Math.Sqrt(hit.SquaredDistance);
            float required = MathF.Max(.0000001f, _thickness - SolveTolerance);
            if (distance >= required || verify)
            {
                ClothStepStatus swept = work.CheckSweep(pair, required, out Vector3 openingNormal);
                if (swept != ClothStepStatus.Accepted)
                {
                    if (verify || swept != ClothStepStatus.UnresolvedContact || openingNormal == Vector3.Zero ||
                        !SeparateSwept(a, b, positions, bias, openingNormal))
                        return swept;
                    continue;
                }
            }

            if (distance > reach) continue;
            Vector3 normal = distance > 0 ? (hit.Residual * (1 / Math.Sqrt(hit.SquaredDistance))).ToVector3() : Vector3.Zero;
            if (!verify && distance < required)
            {
                Vector3 side = pair.Sample(0).Normal;
                if (Vector3.Dot(side, normal) <= 0)
                {
                    if (side == Vector3.Zero || !SeparateSwept(a, b, positions, bias, side))
                        return ClothStepStatus.UnresolvedContact;
                    continue;
                }
            }

            if (verify)
            {
                if (!collectVelocity) continue;
                ClothStepStatus status = velocities.Add(a, hit.Weights, b, hit.OpposingWeights,
                    normal, 0, rig.InverseMasses);
                if (status != ClothStepStatus.Accepted) return status;
                continue;
            }

            float openingDistance = Vector3.Dot(a.Blend(opening, hit.Weights) - b.Blend(opening, hit.OpposingWeights), normal);
            float correction = _thickness + ContactMargin - distance;
            if (correction <= 0) continue;
            float physical = MathF.Max(0, MathF.Min(_thickness + ContactMargin, openingDistance) - distance);
            if (!Separate(a, hit.Weights, b, hit.OpposingWeights, positions, bias, normal, correction, correction - physical))
                return ClothStepStatus.UnresolvedContact;
        }
        return ClothStepStatus.Accepted;
    }

    private bool SeparateSwept(ClothTriangle a, ClothTriangle b, Vector3[] positions, Vector3[] bias, Vector3 normal)
    {
        Span<int> nodesA = [a.A, a.B, a.C];
        Span<int> nodesB = [b.A, b.B, b.C];
        foreach (int nodeA in nodesA)
        foreach (int nodeB in nodesB)
        {
            float distance = Vector3.Dot(positions[nodeA] - positions[nodeB], normal);
            float correction = _thickness + ContactMargin - distance;
            if (correction > 0 && !Separate(new ClothTriangle(nodeA, nodeA, nodeA), Vector3.UnitX,
                new ClothTriangle(nodeB, nodeB, nodeB), Vector3.UnitX, positions, bias, normal, correction, correction))
                return false;
        }
        return true;
    }

    private bool Separate(ClothTriangle a, Vector3 weightsA, ClothTriangle b, Vector3 weightsB,
        Vector3[] positions, Vector3[] bias, Vector3 normal, float correction, float biasCorrection)
    {
        var massA = new Vector3(rig.InverseMasses[a.A], rig.InverseMasses[a.B], rig.InverseMasses[a.C]);
        var massB = new Vector3(rig.InverseMasses[b.A], rig.InverseMasses[b.B], rig.InverseMasses[b.C]);
        Vector3 shareA = massA * weightsA, shareB = massB * weightsB;
        float total = Vector3.Dot(weightsA, shareA) + Vector3.Dot(weightsB, shareB);
        if (total == 0) return correction <= ContactMargin + SolveTolerance;

        Vector3 change = correction / total * normal;
        Vector3 adjustment = biasCorrection / total * normal;
        Apply(a, shareA);
        Apply(b, -shareB);
        return true;

        void Apply(ClothTriangle face, Vector3 share)
        {
            positions[face.A] += share.X * change;
            positions[face.B] += share.Y * change;
            positions[face.C] += share.Z * change;
            bias[face.A] += share.X * adjustment;
            bias[face.B] += share.Y * adjustment;
            bias[face.C] += share.Z * adjustment;
        }
    }
}

internal readonly struct ClothSelfSweep(ClothFacet firstA, ClothFacet lastA, ClothFacet firstB, ClothFacet lastB) : IClothSweepPair
{
    public float Curvature => 0;

    // The two sets are convex at each time. Separation on any fixed axis
    // at both endpoints certifies the whole interval for linear motion.
    internal bool IsOutsideReach(float reach)
    {
        Vector3 centroidDelta = firstA.A + firstA.B + firstA.C - firstB.A - firstB.B - firstB.C;
        return Separated(centroidDelta, reach) || OutsideFacet(firstA, reach) || OutsideFacet(firstB, reach);
    }

    private bool OutsideFacet(in ClothFacet face, float reach)
    {
        Vector3 normal = Vector3.Cross(face.B - face.A, face.C - face.A);
        return Separated(normal, reach) || Separated(Vector3.Cross(normal, face.B - face.A), reach) ||
            Separated(Vector3.Cross(normal, face.C - face.B), reach) || Separated(Vector3.Cross(normal, face.A - face.C), reach);
    }

    private bool Separated(Vector3 axis, float reach)
    {
        float length = axis.Length();
        if (length <= 0 || !float.IsFinite(length)) return false;
        axis /= length;
        return (Gap(0, axis) > reach && Gap(1, axis) > reach) ||
            (Gap(0, -axis) > reach && Gap(1, -axis) > reach);
    }

    public ClothSweepSample Sample(float time)
    {
        ClothFacetContact hit = ClothFacetContact.Closest(firstA.At(lastA, time), firstB.At(lastB, time));
        double distance = Math.Sqrt(hit.SquaredDistance);
        return new ClothSweepSample((float)distance, distance > 0 ? (hit.Residual * (1 / distance)).ToVector3() : Vector3.Zero);
    }

    public float Gap(float time, Vector3 normal) =>
        firstA.At(lastA, time).MinProjection(normal) - firstB.At(lastB, time).MaxProjection(normal);
}
