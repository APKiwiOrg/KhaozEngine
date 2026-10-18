using System;
using System.Numerics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Complete finite convex proof. A failure in any regime or correspondence pass discards
/// every earlier bracket. Operational disagreement is bounded evidence, never a normalized pose.</summary>
internal static class CapsuleConvexSweep
{
    internal static CapsuleSweepResult Sweep(in InstalledSweepLeaf leaf, Vector3 centre,
        float radius, float halfCylinderLength, Vector3 displacement, float maximumErrorMetres,
        ref CapsuleSweepWork work, CapsuleSweepScratch scratch)
    {
        if (!leaf.Map.IsResolved || !float.IsFinite(radius) || radius <= 0 ||
            !float.IsFinite(halfCylinderLength) || halfCylinderLength < 0 ||
            !float.IsFinite(maximumErrorMetres) || maximumErrorMetres < 0 ||
            !CapsuleSweepPath.Point(centre, displacement, 0).IsResolved || !work.Primitive()) return default;
        double length = Math.Sqrt((double)displacement.X * displacement.X +
            (double)displacement.Y * displacement.Y + (double)displacement.Z * displacement.Z);
        if (!float.IsFinite((float)length)) return default;
        double width = length > 0 && maximumErrorMetres > 0 ? Math.Min(1, maximumErrorMetres / (4 * length)) : 1;
        CapsuleSweepResult forward = Prove(leaf, centre, radius, halfCylinderLength, displacement,
            maximumErrorMetres, length, width, ref work, scratch);
        if (!forward.IsComplete || leaf.Map.IsExactlyRigid) return forward;
        InstalledSweepLeaf operational = new(leaf.Geometry, leaf.Pose, leaf.Map, operational: true);
        CapsuleSweepResult local = Prove(operational, centre, radius, halfCylinderLength, displacement,
            maximumErrorMetres, length, width, ref work, scratch);
        if (!local.IsComplete || local.Status != forward.Status) return default;
        if (forward.Status == CapsuleSweepStatus.Clear) return forward;
        if (forward.ImpactDistance is not float a || local.ImpactDistance is not float b) return default;
        float lower = MathF.Min(forward.ClearThroughDistance, local.ClearThroughDistance), upper = MathF.Max(a, b);
        double span = (double)upper - lower;
        float error = (float)span;
        if (error < span) error = MathF.BitIncrement(error);
        return error <= maximumErrorMetres ? new(CapsuleSweepStatus.Hit, lower, upper, error) : default;
    }

    static CapsuleSweepResult Prove(in InstalledSweepLeaf leaf, Vector3 centre, float radius, float halfCylinderLength,
        Vector3 displacement, float maximumError, double length, double width,
        ref CapsuleSweepWork work, CapsuleSweepScratch scratch)
    {
        double lower = double.PositiveInfinity, upper = double.PositiveInfinity;
        bool hit = false;
        for (int feature = 0; feature < leaf.Geometry.FeatureCount; feature++)
            for (int axis = 0; axis < 3; axis++)
            {
                if (!work.Feature() || !CapsuleSweepFeatureDistance.TryBuild(leaf, feature, axis, centre, radius,
                    halfCylinderLength, displacement, scratch, out SweepFeatureRegime regime)) return default;
                // A source-proved empty stratum has no fraction cell to examine. Parallel and
                // zero-length pairs are covered by their retained endpoint/boundary strata.
                if (regime.Empty) continue;
                bool complete = hit
                    ? CapsuleSweepFeaturePolynomial.TryBeforeKnownContact(regime, lower, width, ref work, scratch.Exact, out double from, out double? to)
                    : CapsuleSweepFeaturePolynomial.TryFirstContact(regime, 0, 1, width, ref work, scratch.Exact, out from, out to);
                if (!complete) return default;
                if (hit) lower = Math.Min(lower, from);
                if (to is not double impact) continue;
                hit = true;
                lower = Math.Min(lower, from); upper = Math.Min(upper, impact);
            }
        return hit ? CapsuleSweepDistanceEncoding.Hit(displacement, lower, upper, maximumError)
            : new(CapsuleSweepStatus.Clear, (float)length, null, 0);
    }
}
