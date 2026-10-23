using System;
using System.Numerics;

namespace KhaozEngine.Physics.Bepu;

internal static class ConvexQueryPolicy
{
    internal const int MaximumDescendants = CapsuleGeneralSweepPolicy.MaximumDescendants;
    internal const int MaximumCells = CapsuleGeneralSweepPolicy.MaximumCells;
    internal const int MaximumFeatures = CapsuleGeneralSweepPolicy.MaximumFeatures;
    internal const int MaximumDepth = CapsuleGeneralSweepPolicy.MaximumDepth;
    internal const int MaximumExactBits = CapsuleGeneralSweepPolicy.MaximumExactBits;

    internal static bool Contains(in ConvexQueryRequest request) => request.Shape.IsValid &&
        request.Shape.Radius <= 1000 && request.Shape.LowerY >= 0 && request.Shape.UpperY <= 2000 &&
        request.Pose.Orientation == Quaternion.Identity &&
        InstalledPoseOperator.Within(request.Pose.Position, 1_000_000) &&
        InstalledPoseOperator.Within(request.Displacement, 1_000_000) &&
        request.Operation is >= ConvexQueryOperation.Sweep and <= ConvexQueryOperation.Penetration;
}

/// <summary>Sticky whole-query admission, with primitive-local cells and features. Test limits only lower caps.</summary>
internal struct ConvexQueryWork
{
    readonly int _maximumDescendants, _maximumCells, _maximumFeatures;
    readonly bool _configured;
    internal int Descendants { get; private set; }
    internal int Cells { get; private set; }
    internal int Features { get; private set; }
    internal int CellHighWater { get; private set; }
    internal int FeatureHighWater { get; private set; }
    internal bool Exhausted { get; private set; }

    internal ConvexQueryWork(int maximumDescendants = ConvexQueryPolicy.MaximumDescendants,
        int maximumCells = ConvexQueryPolicy.MaximumCells, int maximumFeatures = ConvexQueryPolicy.MaximumFeatures)
    {
        this = default;
        if (maximumDescendants < 0 || maximumDescendants > ConvexQueryPolicy.MaximumDescendants ||
            maximumCells < 0 || maximumCells > ConvexQueryPolicy.MaximumCells ||
            maximumFeatures < 0 || maximumFeatures > ConvexQueryPolicy.MaximumFeatures)
            throw new ArgumentOutOfRangeException(nameof(maximumDescendants));
        _maximumDescendants = maximumDescendants;
        _maximumCells = maximumCells;
        _maximumFeatures = maximumFeatures;
        _configured = true;
    }

    internal bool Primitive()
    {
        if (Exhausted || Descendants >= (_configured ? _maximumDescendants : ConvexQueryPolicy.MaximumDescendants))
            return Refuse();
        Descendants++;
        Cells = Features = 0;
        return true;
    }

    internal bool Cell()
    {
        if (Exhausted || Cells >= (_configured ? _maximumCells : ConvexQueryPolicy.MaximumCells)) return Refuse();
        Cells++;
        CellHighWater = Math.Max(CellHighWater, Cells);
        return true;
    }

    internal bool Feature()
    {
        if (Exhausted || Features >= (_configured ? _maximumFeatures : ConvexQueryPolicy.MaximumFeatures)) return Refuse();
        Features++;
        FeatureHighWater = Math.Max(FeatureHighWater, Features);
        return true;
    }

    internal bool Refuse() { Exhausted = true; return false; }
}
