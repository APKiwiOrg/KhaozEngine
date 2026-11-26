using System;

namespace KhaozEngine.Attachments;

internal sealed class ClothContactWorkspace(ClothWorkLimits limits)
{
    private readonly ClothAffineHull[] _affine = new ClothAffineHull[limits.MaxHulls];
    private int _affineCount;

    private readonly ClothPosedHull[] _hulls = new ClothPosedHull[limits.MaxHulls];

    internal int Checks { get; private set; }
    internal int Candidates { get; private set; }
    internal int SweepRefinements { get; private set; }
    internal uint? PartId { get; set; }
    private int _hullCount;

    internal ReadOnlySpan<ClothAffineHull> AffineHulls => _affine.AsSpan(0, _affineCount);
    internal ReadOnlySpan<ClothPosedHull> Hulls => _hulls.AsSpan(0, _hullCount);

    internal void Begin()
    {
        Checks = 0;
        Candidates = 0;
        SweepRefinements = 0;
        PartId = null;
        Array.Clear(_affine, 0, _affineCount);
        _affineCount = 0;
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
            bool openingValid = ClothHullPose.TryCreate(hull.Opening, out ClothHullPose opening);
            bool closingValid = ClothHullPose.TryCreate(hull.Closing, out ClothHullPose closing);
            if (!closingValid || (!openingValid && (!closingOnly || allowCoarse)))
            {
                if (!allowCoarse || !HasFallback(hull.Identity, step.ClosingSupports))
                    return ClothStepStatus.UnsupportedHullTransform;
                _affine[_affineCount++] = new ClothAffineHull(hull.Identity, hull.Hull, hull.Closing);
                continue;
            }
            _hulls[_hullCount++] = new ClothPosedHull(hull.Identity, hull.Hull, closingOnly ? closing : opening, closing);
        }

        int rigid = 0, lower = 0;
        foreach (ClothSupport support in step.ClosingSupports)
        {
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

    internal ClothStepStatus CheckSweep<T>(in T pair, float required, out ClothSweepImpact impact)
        where T : struct, IClothSweepPair
    {
        ClothStepStatus status = ClothContactSweep.Check(pair, required, limits.MaxSweepRefinements,
            out int refinements, out impact);
        SweepRefinements += refinements;
        return status;
    }
}
