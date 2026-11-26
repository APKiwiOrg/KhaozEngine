using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal sealed class ClothContactWorkspace(ClothWorkLimits limits)
{
    private readonly ClothPosedHull[] _hulls = new ClothPosedHull[limits.MaxHulls];

    internal int Checks { get; private set; }
    internal int Candidates { get; private set; }
    internal int SweepRefinements { get; private set; }
    internal uint? PartId { get; set; }
    private int _hullCount;

    internal ReadOnlySpan<ClothPosedHull> Hulls => _hulls.AsSpan(0, _hullCount);

    internal void Begin()
    {
        Checks = 0;
        Candidates = 0;
        SweepRefinements = 0;
        PartId = null;
        Array.Clear(_hulls, 0, _hullCount);
        _hullCount = 0;
    }

    internal ClothStepStatus Prepare(in ClothSubstep step, bool closingOnly, bool allowCoarse)
    {
        if (step.ClosingSupports.Length > limits.MaxSupports)
        {
            PartId = step.ClosingSupports[limits.MaxSupports].Identity.PartId;
            return ClothStepStatus.SupportCapacity;
        }
        if (step.Hulls.Length > _hulls.Length)
        {
            PartId = step.Hulls[_hulls.Length].Identity.PartId;
            return ClothStepStatus.SupportCapacity;
        }
        foreach (ClothHullSupport hull in step.Hulls)
        {
            PartId = hull.Identity.PartId;
            if (!ClothHullPose.TryCreate(hull.Opening, out ClothHullPose opening) ||
                !ClothHullPose.TryCreate(hull.Closing, out ClothHullPose closing))
            {
                if (!allowCoarse || !HasFallback(hull.Identity, step.ClosingSupports))
                    return ClothStepStatus.UnsupportedHullTransform;
                continue;
            }
            _hulls[_hullCount++] = new ClothPosedHull(hull.Identity, hull.Hull, closingOnly ? closing : opening, closing);
        }

        int rigid = 0, lower = 0;
        foreach (ClothSupport support in step.ClosingSupports)
        {
            if (Replaced(support)) continue;
            if (support.IsLowerSegment ? ++lower > limits.MaxLowerSegments : ++rigid > limits.MaxRigidSupports)
            {
                PartId = support.Identity.PartId;
                return ClothStepStatus.SupportCapacity;
            }
        }
        PartId = null;
        return ClothStepStatus.Accepted;
    }

    internal bool Replaced(in ClothSupport support)
    {
        if (support.IsPlane || support.IsLowerSegment) return false;
        foreach (ClothPosedHull hull in Hulls)
            if (hull.Identity.SameGroup(support.Identity)) return true;
        return false;
    }

    private static bool HasFallback(in ClothSupportIdentity id, ReadOnlySpan<ClothSupport> supports)
    {
        foreach (ClothSupport support in supports)
            if (!support.IsPlane && !support.IsLowerSegment && id.SameGroup(support.Identity)) return true;
        return false;
    }

    internal bool Visit()
    {
        if (Checks == limits.MaxContactChecks) return false;
        Checks++;
        return true;
    }

    internal bool Candidate()
    {
        if (Candidates == limits.MaxContactCandidates) return false;
        Candidates++;
        return true;
    }

    internal ClothStepStatus CheckSweep<T>(in T pair, float required, out Vector3 normal)
        where T : struct, IClothSweepPair
    {
        ClothStepStatus status = ClothContactSweep.Check(pair, required, limits.MaxSweepRefinements,
            out int refinements, out normal);
        SweepRefinements += refinements;
        return status;
    }
}
