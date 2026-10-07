using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities;

namespace KhaozEngine.Physics.Bepu;

public sealed partial class BepuPhysicsWorld : IPhysicsCapsuleSweep
{
    const int MaximumSweepCandidates = 4096;
    const float MaximumSweepErrorMetres = 0.001f;
    readonly List<CollidableReference> _sweepCandidates = new();

    /// <inheritdoc/>
    public CapsuleSweepResult SweepCapsuleCertified(CapsuleShape capsule, Pose pose, Vector3 displacement,
        QueryFilter filter = default) => SweepCapsuleCertifiedCore(capsule, pose, displacement, filter, null);

    CapsuleSweepResult SweepCapsuleCertifiedCore(CapsuleShape capsule, Pose pose, Vector3 displacement,
        QueryFilter filter, StaticQueryExclusions? exclusions)
    {
        using QueryOperation operation = EnterQuery();
        if (capsule is null || !float.IsFinite(capsule.Radius) || capsule.Radius <= 0 ||
            !float.IsFinite(capsule.Length) || capsule.Length < 0 || pose.Orientation != Quaternion.Identity ||
            filter.Layers != 0 || filter.Mobility is not (QueryMobility.All or QueryMobility.Statics or QueryMobility.Dynamics) ||
            !_sweepEvidence.IsCurrent(this, _queryGeneration) || !SweepRecordCountsMatch() ||
            !SelectedSweepBoundsProved(filter.Mobility, exclusions)) return default;
        float halfCylinder = capsule.Length * 0.5f;
        if (!CapsuleSweepAperture.TryCreate(pose.Position, capsule.Radius, halfCylinder, displacement,
            out Vector3 min, out Vector3 max)) return default;
        double length = System.Math.Sqrt((double)displacement.X * displacement.X +
            (double)displacement.Y * displacement.Y + (double)displacement.Z * displacement.Z);
        float representedLength = (float)length;
        if (!float.IsFinite(representedLength)) return default;

        // Both passes retain the identical aperture/tree interval. The checked A-first preflight
        // bounds stack/work before the pinned raw enumerator accesses its private leaf references.
        if (!CapsuleTreePreflight.TryValidate(in _sim.BroadPhase.ActiveTree, min, max,
                CapsuleTreePreflight.MaximumNodes, CapsuleTreePreflight.MaximumLeaves, CapsuleTreePreflight.MaximumPending,
                out _, out _, out _) ||
            !CapsuleTreePreflight.TryValidate(in _sim.BroadPhase.StaticTree, min, max,
                CapsuleTreePreflight.MaximumNodes, CapsuleTreePreflight.MaximumLeaves, CapsuleTreePreflight.MaximumPending,
                out _, out _, out _)) return default;
        _sweepCandidates.Clear();
        var collector = new SweepCandidates(_sweepCandidates, filter.Mobility, exclusions);
        _sim.BroadPhase.GetOverlaps(min, max, ref collector);
        if (collector.Overflow) return default;

        bool hit = false;
        float lower = float.PositiveInfinity, upper = float.PositiveInfinity, error = 0;
        foreach (CollidableReference candidate in _sweepCandidates)
        {
            if (!TryGetSweepLeaf(candidate, out TypedIndex shape, out RigidPose bodyPose)) return default;
            CapsuleSweepResult leaf = CapsuleBoxSweep.Sweep(_sim.Shapes, shape, bodyPose, pose.Position,
                capsule.Radius, halfCylinder, displacement, MaximumSweepErrorMetres, CapsuleBoxFractionSweep.MaximumCells);
            if (!leaf.IsComplete || leaf.CertifiedErrorMetres > MaximumSweepErrorMetres) return default;
            error = System.MathF.Max(error, leaf.CertifiedErrorMetres);
            if (leaf.Status == CapsuleSweepStatus.Clear)
            {
                if (leaf.ClearThroughDistance != representedLength) return default;
                continue;
            }
            if (leaf.ImpactDistance is not float impact || impact > length || leaf.ClearThroughDistance > length)
                return default;
            hit = true;
            lower = System.MathF.Min(lower, leaf.ClearThroughDistance);
            upper = System.MathF.Min(upper, impact);
        }
        // Minima of complete individual brackets enclose the earliest event. Even Hit(0) cannot
        // bypass an unresolved later candidate, since the complete active constraints are required.
        return hit ? new(CapsuleSweepStatus.Hit, lower, upper, error)
            : new(CapsuleSweepStatus.Clear, representedLength, null, error);
    }

    bool TryGetSweepLeaf(CollidableReference candidate, out TypedIndex shape, out RigidPose pose)
    {
        shape = default;
        pose = default;
        if (candidate.Mobility == CollidableMobility.Static)
        {
            if (!_reverseHandles.TryGetValue(candidate.StaticHandle.Value, out int id) ||
                !_sweepStaticBounds.TryGetValue(id, out bool proved) || !proved) return false;
            _sim.Statics.GetDescription(candidate.StaticHandle, out StaticDescription description);
            shape = description.Shape;
            pose = description.Pose;
        }
        else
        {
            if (!_reverseDynamics.TryGetValue(candidate.BodyHandle.Value, out int id) ||
                !_sweepDynamicBounds.TryGetValue(id, out bool proved) || !proved) return false;
            var body = _sim.Bodies.GetBodyReference(candidate.BodyHandle);
            shape = body.Collidable.Shape;
            pose = body.Pose;
        }
        return true;
    }

    struct SweepCandidates(List<CollidableReference> target, QueryMobility mobility,
        StaticQueryExclusions? exclusions) : IBreakableForEach<CollidableReference>
    {
        public bool Overflow;
        int examined;

        public bool LoopBody(CollidableReference reference)
        {
            if (Overflow || examined == MaximumSweepCandidates) { Overflow = true; return false; }
            examined++;
            bool isStatic = reference.Mobility == CollidableMobility.Static;
            if (mobility == QueryMobility.Statics && !isStatic || mobility == QueryMobility.Dynamics && isStatic)
                return true;
            if (exclusions is not null && !exclusions.Allows(reference)) return true;
            target.Add(reference);
            return true;
        }
    }
}
