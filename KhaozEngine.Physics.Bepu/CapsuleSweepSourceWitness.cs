using System;
using System.Numerics;
using BepuPhysics;
using BepuUtilities;

namespace KhaozEngine.Physics.Bepu;

/// <summary>A real pinned-backend sample can witness contact, never whole-path separation.
/// Only an exactly rigid signed permutation admits the unchanged local capsule metric.</summary>
internal readonly struct CapsuleSweepSourceSample
{
    internal Vector3 Centre { get; }
    internal Vector3 Axis { get; }
    internal bool IsResolved { get; }
    CapsuleSweepSourceSample(Vector3 centre, Vector3 axis) { Centre = centre; Axis = axis; IsResolved = true; }

    internal static bool TryCreate(in InstalledSweepLeaf leaf, Vector3 centre, Vector3 delta,
        float fraction, out CapsuleSweepSourceSample sample)
    {
        sample = default;
        if (!leaf.Map.IsExactlyRigid || !Permutation(leaf.Map.X, leaf.Map.RepresentedX) ||
            !Permutation(leaf.Map.Y, leaf.Map.RepresentedY) || !Permutation(leaf.Map.Z, leaf.Map.RepresentedZ) ||
            !float.IsFinite(fraction) || fraction < 0 || fraction > 1) return false;
        RigidPose root = leaf.Geometry.ParentPose;
        Vector3Wide.Broadcast(root.Position, out var position);
        Vector3Wide.Broadcast(centre, out var query);
        Vector3Wide.Subtract(position, query, out var offset);
        Vector3Wide.Broadcast(-delta, out var velocity);
        Vector3Wide.Scale(velocity, new Vector<float>(fraction), out var movement);
        Vector3Wide.Add(offset, movement, out offset);
        Vector3Wide.Broadcast(leaf.Geometry.LocalPose.Position, out var child);
        QuaternionWide.TransformWithoutOverlap(child, Broadcast(root.Orientation), out var rotated);
        Vector3Wide.Add(offset, rotated, out offset);
        // ConvexSweepTaskCommon.ConstructSamples adds the child after the root-relative
        // path. Transform its actual result, rather than subtracting the composed pose.
        QuaternionWide.Conjugate(Broadcast(leaf.Pose.Orientation), out var conjugate);
        Vector3Wide.Negate(offset, out var relative);
        QuaternionWide.TransformWithoutOverlap(relative, conjugate, out var local);
        Vector3Wide.Broadcast(Vector3.UnitY, out var axis);
        QuaternionWide.TransformWithoutOverlap(axis, conjugate, out var localAxis);
        Vector3 c = new(local.X[0], local.Y[0], local.Z[0]), a = new(localAxis.X[0], localAxis.Y[0], localAxis.Z[0]);
        if (!InstalledPoseOperator.Within(c, CapsuleGeneralSweepPolicy.MaximumStoredCoordinate) ||
            a != new Vector3((float)leaf.Map.RepresentedX.Y.Value, (float)leaf.Map.RepresentedY.Y.Value, (float)leaf.Map.RepresentedZ.Y.Value)) return false;
        for (int i = 1; i < Vector<float>.Count; i++)
            if (local.X[i] != c.X || local.Y[i] != c.Y || local.Z[i] != c.Z ||
                localAxis.X[i] != a.X || localAxis.Y[i] != a.Y || localAxis.Z[i] != a.Z) return false;
        sample = new(c, a);
        return true;
    }

    static bool Permutation(FeaturePoint real, FeaturePoint represented) => real.IsExact && represented.IsExact &&
        real.X.Value == represented.X.Value && real.Y.Value == represented.Y.Value && real.Z.Value == represented.Z.Value &&
        Component(real.X.Value) && Component(real.Y.Value) && Component(real.Z.Value);
    static bool Component(double value) => value is -1 or 0 or 1;
    static QuaternionWide Broadcast(Quaternion q) => new() { X = new(q.X), Y = new(q.Y), Z = new(q.Z), W = new(q.W) };
}

internal static class CapsuleSweepSourceWitness
{
    internal static double? Find(in InstalledSweepLeaf leaf, Vector3 centre, float radius, float half,
        Vector3 delta, double length, in CapsuleSweepResult forward, ref CapsuleSweepWork work, CapsuleSweepScratch scratch)
    {
        if (!leaf.Map.IsExactlyRigid || forward.Status != CapsuleSweepStatus.Hit || forward.ImpactDistance is not float impact) return null;
        float upper = length == 0 ? 0 : (float)Math.Min(1, impact / length);
        float middle = length == 0 ? 0 : (float)Math.Min(1, ((double)forward.ClearThroughDistance + impact) / (2 * length));
        // The upper proposal, bracket midpoint and immediate entering neighbor are
        // existence witnesses only. The operational pass still proves every earlier cell.
        for (int candidate = 0; candidate < 3; candidate++)
        {
            float fraction = candidate == 0 ? upper : candidate == 1 ? middle : MathF.BitIncrement(upper);
            if (fraction > 1 || (candidate > 0 && fraction == upper)) continue;
            if (!CapsuleSweepSourceSample.TryCreate(leaf, centre, delta, fraction, out var sample)) return null;
            for (int feature = 0; feature < leaf.Geometry.FeatureCount; feature++)
                for (int axis = 0; axis < 3; axis++)
                {
                    if (!work.Feature()) return null;
                    if (!CapsuleSweepFeatureDistance.TryBuild(leaf, feature, axis, centre, radius, half,
                        Vector3.Zero, scratch, out var regime, sample) || regime.Empty) continue;
                    if (CapsuleSweepFeaturePolynomial.TryFirstContact(regime, 0, 0, 1, ref work, scratch.Exact,
                        out _, out double? contact) && contact == 0) return fraction;
                    if (work.Exhausted) return null;
                }
            if (length == 0) break;
        }
        if (length == 0) return null;
        InstalledSweepLeaf forwardLeaf = new(leaf.Geometry, leaf.Pose, leaf.Map);
        for (int feature = 0; feature < leaf.Geometry.FeatureCount; feature++)
            for (int axis = 0; axis < 3; axis++)
            {
                if (!work.Feature()) return null;
                if (!CapsuleSweepFeatureDistance.TryBuild(forwardLeaf, feature, axis, centre, radius, half,
                    delta, scratch, out var regime) || regime.Empty) continue;
                GeometryInterval a = scratch.Exact.Bounds(regime.A), b = scratch.Exact.Bounds(regime.B);
                if (!a.IsResolved || a.Lower <= 0) continue;
                // A quadratic minimum proposes a sample for an isolated tangency. Its
                // rounded quotient proves nothing until the actual source feature below
                // supplies a closed contact witness through the existing exact predicate.
                GeometryInterval quotient = b.Multiply(GeometryInterval.Exact(-1)).Divide(a.Multiply(GeometryInterval.Exact(2)));
                if (!quotient.IsResolved) continue;
                float fraction = (float)(quotient.Lower + (quotient.Upper - quotient.Lower) * 0.5);
                if (!CapsuleSweepSourceSample.TryCreate(leaf, centre, delta, fraction, out var sample)) continue;
                if (!work.Feature()) return null;
                if (CapsuleSweepFeatureDistance.TryBuild(leaf, feature, axis, centre, radius, half,
                    Vector3.Zero, scratch, out var source, sample) && !source.Empty &&
                    CapsuleSweepFeaturePolynomial.TryFirstContact(source, 0, 0, 1, ref work, scratch.Exact,
                        out _, out double? contact) && contact == 0) return fraction;
                if (work.Exhausted) return null;
            }
        return null;
    }
}
