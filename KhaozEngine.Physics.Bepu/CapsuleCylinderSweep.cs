using System;
using System.Numerics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Bounded whole-cell cylinder proof. Circular support covers every required feature.
/// Closed point-feature witnesses establish existence, never separation or a Clear prefix.</summary>
internal static class CapsuleCylinderSweep
{
    readonly record struct Cell(double From, double To, int Depth);

    internal static CapsuleSweepResult Sweep(in InstalledSweepLeaf leaf, Vector3 centre,
        in CapsuleQueryDimensions capsule, Vector3 displacement, ref CapsuleSweepWork work, CapsuleSweepScratch scratch)
    {
        if (!leaf.Map.IsResolved || !CapsuleSweepPath.Point(centre, displacement, 0).IsResolved || !work.Primitive()) return default;
        scratch.Exact.Reset();
        double length = Math.Sqrt((double)displacement.X * displacement.X + (double)displacement.Y * displacement.Y +
            (double)displacement.Z * displacement.Z);
        if (!float.IsFinite((float)length)) return default;
        double width = length > 0 ? Math.Min(1, CapsuleGeneralSweepPolicy.MaximumErrorMetres / (4 * length)) : 1;
        if (!CapsuleCylinderFrame.TryCreate(leaf, centre, displacement, capsule, out var forwardFrame)) return default;
        double? seed = SideWitness(leaf, centre, displacement, capsule, width, ref work, scratch);
        if (work.Exhausted || !scratch.Exact.IsResolved) return default;
        CapsuleSweepResult forward = Prove(forwardFrame, displacement, length, width, seed, ref work, scratch);
        if (!forward.IsComplete) return default;
        InstalledSweepLeaf operational = new(leaf.Geometry, leaf.Pose, leaf.Map, operational: true);
        if (!CapsuleCylinderFrame.TryCreate(operational, centre, displacement, capsule, out var localFrame)) return default;
        seed = CapsuleSweepSourceWitness.Find(operational, centre, capsule.Radius, capsule.Length * 0.5f,
            displacement, length, forward, ref work, scratch, cylindricalSide: true);
        // A cap or rim source sample is also a genuine witness. Its entire preceding
        // interval still goes through the independent operational support proof.
        if (!seed.HasValue && forward.Status == CapsuleSweepStatus.Hit && forward.ImpactDistance is float impact)
            seed = SourceWitness(operational, localFrame, centre, displacement, length, forward.ClearThroughDistance, impact, ref work, scratch);
        if (work.Exhausted || !scratch.Exact.IsResolved) return default;
        CapsuleSweepResult local = Prove(localFrame, displacement, length, width, seed, ref work, scratch);
        if (!local.IsComplete || local.Status != forward.Status) return default;
        if (forward.Status == CapsuleSweepStatus.Clear) return forward;
        if (forward.ImpactDistance is not float a || local.ImpactDistance is not float b) return default;
        float lo = MathF.Min(forward.ClearThroughDistance, local.ClearThroughDistance), hi = MathF.Max(a, b);
        double span = (double)hi - lo;
        float error = (float)span;
        if (error < span) error = MathF.BitIncrement(error);
        return error <= CapsuleGeneralSweepPolicy.MaximumErrorMetres ? new(CapsuleSweepStatus.Hit, lo, hi, error) : default;
    }

    static double? SideWitness(in InstalledSweepLeaf leaf, Vector3 centre, Vector3 delta,
        in CapsuleQueryDimensions capsule, double width, ref CapsuleSweepWork work, CapsuleSweepScratch scratch)
    {
        // Only an exactly rigid forward axis has the unchanged radial metric. This is
        // a finite side feature, not the hemispherical ends of a capsule substitute.
        if (!leaf.Map.IsExactlyRigid) return null;
        double? upper = null;
        for (int axis = 0; axis < 3; axis++)
        {
            if (!work.Feature() || !CapsuleSweepFeatureDistance.TryBuild(leaf, 0, axis, centre, capsule.Radius,
                capsule.Length * 0.5f, delta, scratch, out var regime, cylindricalSide: true)) return null;
            if (regime.Empty) continue;
            if (!CapsuleSweepFeaturePolynomial.TryFirstContact(regime, 0, 1, width, ref work, scratch.Exact, out _, out var hit)) return null;
            if (hit.HasValue) upper = upper.HasValue ? Math.Min(upper.Value, hit.Value) : hit;
        }
        return upper;
    }

    static double? SourceWitness(in InstalledSweepLeaf leaf, in CapsuleCylinderFrame frame, Vector3 centre,
        Vector3 delta, double length, float lower, float upper, ref CapsuleSweepWork work, CapsuleSweepScratch scratch)
    {
        for (int candidate = 0; candidate < 3; candidate++)
        {
            float t = length == 0 ? 0 : (float)Math.Min(1, candidate == 1 ? ((double)lower + upper) / (2 * length) : upper / length);
            if (candidate == 2) t = MathF.BitIncrement(t);
            if (!CapsuleSweepSourceSample.TryCreate(leaf, centre, delta, t, out var sample)) continue;
            CapsuleCylinderFrame sampled = frame with
            {
                Centre = FeaturePoint.Exact(sample.Centre),
                Delta = FeaturePoint.Exact(Vector3.Zero),
                QueryAxis = FeaturePoint.Exact(sample.Axis)
            };
            if (!CapsuleCylinderFeatures.TryPropose(sampled, 0, ref work, out var proposal)) return null;
            if (CapsuleCylinderFeatures.Contact(sampled, 0, proposal.AxisParameter, scratch.Exact)) return t;
        }
        return null;
    }

    static CapsuleSweepResult Prove(in CapsuleCylinderFrame frame, Vector3 delta, double length, double width,
        double? witness, ref CapsuleSweepWork work, CapsuleSweepScratch scratch)
    {
        if (!CapsuleCylinderFeatures.TryPropose(frame, 0, ref work, out var initial)) return default;
        if (CapsuleCylinderFeatures.Contact(frame, 0, initial.AxisParameter, scratch.Exact))
            return CapsuleSweepDistanceEncoding.Hit(delta, 0, 0, CapsuleGeneralSweepPolicy.MaximumErrorMetres);
        if (!scratch.Exact.IsResolved) return default;
        Span<Cell> cells = stackalloc Cell[CapsuleGeneralSweepPolicy.MaximumDepth + 1];
        double to = witness ?? 1;
        cells[0] = new(0, to, 0);
        int count = 1;
        while (count > 0)
        {
            if (!work.Cell()) return default;
            Cell cell = cells[--count];
            double middle = cell.From + (cell.To - cell.From) * 0.5;
            if (!CapsuleCylinderFeatures.TryPropose(frame, middle, ref work, out var proposal)) return default;
            if (CapsuleCylinderFeatures.Separated(frame, proposal.Normal, cell.From, cell.To)) continue;
            if (cell.To - cell.From <= width)
            {
                double candidate = Math.Min(to, cell.From + width);
                if (witness is double known && known - cell.From <= width)
                    return CapsuleSweepDistanceEncoding.Hit(delta, cell.From, known, CapsuleGeneralSweepPolicy.MaximumErrorMetres);
                if (!CapsuleCylinderFeatures.TryPropose(frame, candidate, ref work, out var contact)) return default;
                if (CapsuleCylinderFeatures.Contact(frame, candidate, contact.AxisParameter, scratch.Exact))
                    return CapsuleSweepDistanceEncoding.Hit(delta, cell.From, candidate, CapsuleGeneralSweepPolicy.MaximumErrorMetres);
                if (!scratch.Exact.IsResolved) return default;
            }
            if (cell.Depth == CapsuleGeneralSweepPolicy.MaximumDepth || !(middle > cell.From && middle < cell.To)) return default;
            cells[count++] = new(middle, cell.To, cell.Depth + 1);
            cells[count++] = new(cell.From, middle, cell.Depth + 1);
        }
        return witness.HasValue ? CapsuleSweepDistanceEncoding.Hit(delta, to, to, CapsuleGeneralSweepPolicy.MaximumErrorMetres)
            : new(CapsuleSweepStatus.Clear, (float)length, null, 0);
    }
}
