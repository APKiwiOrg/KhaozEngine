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

    /// <inheritdoc/>
    public CapsuleSweepResult SweepCapsuleCertified(CapsuleShape capsule, Pose pose, Vector3 displacement,
        QueryFilter filter = default) => SweepCapsuleCertifiedCore(capsule, pose, displacement, filter, null);

    /// <inheritdoc/>
    public CapsuleSweepResult SweepCapsuleCertified(in CapsuleQueryDimensions capsule, Pose pose, Vector3 displacement,
        QueryFilter filter = default) => SweepCapsuleCertifiedCore(capsule, pose, displacement, filter, null);

    CapsuleSweepResult SweepCapsuleCertifiedCore(CapsuleShape capsule, Pose pose, Vector3 displacement,
        QueryFilter filter, StaticQueryExclusions? exclusions)
    {
        if (capsule is null)
        {
            using QueryOperation operation = EnterQuery();
            return default;
        }
        return SweepCapsuleCertifiedCore(new CapsuleQueryDimensions(capsule.Radius, capsule.Length),
            pose, displacement, filter, exclusions);
    }

    CapsuleSweepResult SweepCapsuleCertifiedCore(in CapsuleQueryDimensions capsule, Pose pose, Vector3 displacement,
        QueryFilter filter, StaticQueryExclusions? exclusions)
    {
        using QueryOperation operation = EnterQuery();
        if (!float.IsFinite(capsule.Radius) || capsule.Radius <= 0 ||
            !float.IsFinite(capsule.Length) || capsule.Length < 0 || pose.Orientation != Quaternion.Identity ||
            filter.Layers != 0 || filter.Mobility is not (QueryMobility.All or QueryMobility.Statics or QueryMobility.Dynamics) ||
            !_sweepEvidence.IsCurrent(this, _queryGeneration) || !SweepRecordCountsMatch() ||
            !SelectedSweepBoundsProved(filter.Mobility, exclusions)) return default;
        float halfCylinder = capsule.Length * 0.5f;
        bool hasGeneral = _sweepSelection.HasGeneral(filter.Mobility, exclusions);
        if (hasGeneral && !CapsuleGeneralSweepPolicy.ContainsQuery(pose.Position, capsule.Radius, halfCylinder, displacement)) return default;
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
        QueryScratch scratch = operation.Scratch;
        CapsuleSweepScratch? general = scratch.GeneralSweeps.Take();
        if (general is null) return default;
        List<CollidableReference> candidates = scratch.SweepCandidates.Take();
        try
        {
            candidates.Clear();
            var collector = new SweepCandidates(candidates, filter.Mobility, exclusions, this, generalOnly: false);
            _sim.BroadPhase.GetOverlaps(min, max, ref collector);
            if (collector.Overflow) return default;
            if (hasGeneral)
            {
                var bounds = _sweepSelection.Bounds(filter.Mobility, exclusions);
                if (!CapsuleSweepPruning.TryQueryPad(pose.Position, capsule.Radius, halfCylinder, displacement, bounds, out double pad) ||
                    !CapsuleSweepPruning.ExpandAperture(min, max, pad, out var generalMin, out var generalMax)) return default;
                // Preflight and enumeration share the same general-only selection under
                // this read interval. Identity leaves consume none of its leaf budget.
                var preflight = new SweepCandidates(null, filter.Mobility, exclusions, this, generalOnly: true);
                if (!VisitGeneralSweepTrees(generalMin, generalMax, ref preflight)) return default;
                var selected = new SweepCandidates(candidates, filter.Mobility, exclusions, this, generalOnly: true);
                if (!VisitGeneralSweepTrees(generalMin, generalMax, ref selected)) return default;
            }
            return CertifyCandidates(candidates, capsule, pose, halfCylinder, displacement, length,
                representedLength, min, max, filter, general);
        }
        finally { scratch.GeneralSweeps.Restore(general); scratch.SweepCandidates.Restore(candidates); }
    }

    CapsuleSweepResult CertifyCandidates(List<CollidableReference> candidates, in CapsuleQueryDimensions capsule,
        Pose pose, float halfCylinder, Vector3 displacement, double length, float representedLength,
        Vector3 queryMin, Vector3 queryMax, QueryFilter filter, CapsuleSweepScratch scratch)
    {
        var work = new CapsuleSweepWork();
        bool hit = false;
        float lower = float.PositiveInfinity, upper = float.PositiveInfinity, error = 0;
        foreach (CollidableReference candidate in candidates)
        {
            if (!TryGetSweepLeaf(candidate, out TypedIndex shape, out RigidPose bodyPose)) return default;
            CapsuleSweepResult leaf;
            if (CapsuleSweepGeometry.TryReadIdentityBox(_sim.Shapes, shape, bodyPose, out _))
            {
                if (!work.Primitive()) return default;
                leaf = CapsuleBoxSweep.Sweep(_sim.Shapes, shape, bodyPose, pose.Position,
                    capsule.Radius, halfCylinder, displacement, MaximumSweepErrorMetres, CapsuleBoxFractionSweep.MaximumCells);
            }
            else
            {
                if (!TryGetInstalledSweepGeometry(candidate, out InstalledSweepGeometry? geometry) || geometry is null)
                    return default;
                if (!geometry.Matches(_sim.Shapes, shape) || !geometry.TryPose(bodyPose, out _) ||
                    geometry.PruningBounds is not { } bounds) return default;
                if (!CapsuleSweepPruning.TryQueryPad(pose.Position, capsule.Radius, halfCylinder, displacement, geometry.PruningMetrics,
                    out double queryPad, includePreimage: false)) return default;
                if (!CapsuleSweepPruning.ExpandAperture(bounds.Min, bounds.Max, queryPad, out var requiredMin, out var requiredMax))
                    return default;
                if (!BoundingBox.Intersects(queryMin, queryMax, requiredMin, requiredMax)) continue;
                leaf = CapsuleGeneralSweep.Sweep(_sim.Shapes, shape, bodyPose, geometry, capsule, pose.Position,
                    displacement, filter, ref work, scratch);
            }
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

    bool VisitGeneralSweepTrees(Vector3 min, Vector3 max, ref SweepCandidates collector)
    {
        if (!CapsuleGeneralSweepTraversal.TryValidate(_sim.BroadPhase.ActiveTree, min, max) ||
            !CapsuleGeneralSweepTraversal.TryValidate(_sim.BroadPhase.StaticTree, min, max)) return false;
        _sim.BroadPhase.GetOverlaps(min, max, ref collector);
        return !collector.Overflow;
    }

    bool IsGeneralSweepCandidate(CollidableReference reference) => reference.Mobility == CollidableMobility.Static
        ? _reverseHandles.TryGetValue(reference.StaticHandle.Value, out int id) && _sweepSelection.Contains(id, dynamic: false)
        : _reverseDynamics.TryGetValue(reference.BodyHandle.Value, out int dynamicId) && _sweepSelection.Contains(dynamicId, dynamic: true);

    struct SweepCandidates(List<CollidableReference>? target, QueryMobility mobility,
        StaticQueryExclusions? exclusions, BepuPhysicsWorld owner, bool generalOnly) : IBreakableForEach<CollidableReference>
    {
        public bool Overflow;
        int examined;

        public bool LoopBody(CollidableReference reference)
        {
            if (!generalOnly)
            {
                if (Overflow || examined == MaximumSweepCandidates) { Overflow = true; return false; }
                examined++;
            }
            bool isStatic = reference.Mobility == CollidableMobility.Static;
            if (mobility == QueryMobility.Statics && !isStatic || mobility == QueryMobility.Dynamics && isStatic)
                return true;
            if (exclusions is not null && !exclusions.Allows(reference)) return true;
            if (owner.IsGeneralSweepCandidate(reference) != generalOnly) return true;
            if (generalOnly)
            {
                if (Overflow || examined == MaximumSweepCandidates) { Overflow = true; return false; }
                examined++;
            }
            target?.Add(reference);
            return true;
        }
    }
}
