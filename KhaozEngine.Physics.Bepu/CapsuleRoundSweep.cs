using System.Numerics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Point/closed-segment distance with the exact sum of both round radii.
/// Finite regimes, closed roots, correspondence and refinement share the convex proof.</summary>
internal static class CapsuleRoundSweep
{
    internal static CapsuleSweepResult Sweep(in InstalledSweepLeaf leaf, Vector3 centre,
        in CapsuleQueryDimensions capsule, Vector3 displacement, ref CapsuleSweepWork work, CapsuleSweepScratch scratch) =>
        leaf.Geometry.IsRound ? CapsuleConvexSweep.Sweep(leaf, centre, capsule.Radius, capsule.Length * 0.5f,
            displacement, CapsuleGeneralSweepPolicy.MaximumErrorMetres, ref work, scratch) : default;
}
