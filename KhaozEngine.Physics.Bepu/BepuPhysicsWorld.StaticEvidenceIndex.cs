using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities;
using KhaozEngine.Physics;
using StaticHandle = KhaozEngine.Physics.StaticHandle;

namespace KhaozEngine.Physics.Bepu;

public sealed partial class BepuPhysicsWorld
{
    readonly Dictionary<int, (InstalledSweepGeometry? Geometry, RigidPose Pose, InstalledStaticEvidence Evidence)> _staticEvidence = new();
    readonly HashSet<int> _unprovedStaticEvidenceBounds = new();

    void PublishStaticEvidence(int id, InstalledSweepGeometry? geometry, RigidPose pose)
    {
        // Called only by the existing static writer publication. Rebases rebuild poses and indices together.
        var handle = _handles[id].Handle;
        int broad = _sim.Statics[_sim.Statics.HandleToIndex[handle.Value]].BroadPhaseIndex;
        if (ReadSweepStoredBounds(_sim.BroadPhase.StaticTree, broad, out var min, out var max) &&
            SweepAncestorPath(_sim.BroadPhase.StaticTree, broad, min, max, containment: true))
            _unprovedStaticEvidenceBounds.Remove(id);
        else _unprovedStaticEvidenceBounds.Add(id);
        _staticEvidence.TryGetValue(id, out var prior);
        _staticEvidence[id] = (geometry, pose, BuildStaticEvidence(new(id), geometry, pose, prior.Evidence));
    }

    InstalledStaticEvidence BuildStaticEvidence(StaticHandle owner, InstalledSweepGeometry? geometry, RigidPose root,
        InstalledStaticEvidence? prior)
    {
        var evidence = prior is not null && ReferenceEquals(prior.Geometry, geometry) ? prior : new(geometry);
        evidence.Root = root;
        if (geometry is null) { evidence.Status = PhysicsEvidenceStatus.Unsupported; return evidence; }
        if (geometry.Children is { } children)
        {
            if (evidence.Children.Length != children.Length) evidence.Children = new InstalledStaticEvidence[children.Length];
            for (int i = 0; i < children.Length; i++)
                evidence.Children[i] = BuildStaticEvidence(owner, children[i], root, evidence.Children[i]);
            return evidence;
        }
        if (geometry.Mesh is { } mesh)
        {
            if (!geometry.TryComposePose(root, out var pose) || !InstalledPoseOperator.TryCreateSupport(pose, out var map))
            { evidence.Status = PhysicsEvidenceStatus.Unsupported; return evidence; }
            if (!mesh.ProvePose(pose, map)) { evidence.Status = PhysicsEvidenceStatus.Unresolved; return evidence; }
            evidence.Mesh ??= new(mesh);
            evidence.Mesh.Refresh(pose, map);
            evidence.Status = PhysicsEvidenceStatus.Complete;
            return evidence;
        }
        int count = 0;
        evidence.Status = EmitEvidence(owner, geometry, root, new(float.MinValue), new(float.MaxValue),
            int.MaxValue, ref count, Ignore);
        evidence.Features = count;
        return evidence;
        static bool Ignore(in PhysicsEvidenceFace face, ReadOnlySpan<PhysicsEvidencePoint> points) => true;
    }

    PhysicsEvidenceStatus EmitIndexedEvidence(StaticHandle owner, InstalledStaticEvidence evidence, bool excluded,
        Vector3 minimum, Vector3 maximum, int limit, ref int count, PhysicsEvidenceVisitor visitor, List<int> triangles)
    {
        foreach (var child in evidence.Children)
        {
            var status = EmitIndexedEvidence(owner, child, excluded, minimum, maximum, limit, ref count, visitor, triangles);
            if (status != PhysicsEvidenceStatus.Complete) return status;
        }
        if (evidence.Geometry?.Children is not null) return PhysicsEvidenceStatus.Complete;
        if (evidence.Geometry is null) return evidence.Status;
        if (evidence.Geometry.Mesh is not null)
        {
            if (evidence.Status != PhysicsEvidenceStatus.Complete || evidence.Mesh is not { } mesh) return evidence.Status;
            return excluded ? mesh.Status : mesh.Emit(owner, minimum, maximum, limit, ref count, visitor, triangles);
        }
        return excluded ? evidence.Status :
            EmitEvidence(owner, evidence.Geometry, evidence.Root, minimum, maximum, limit, ref count, visitor);
    }

    PhysicsEvidenceStatus CaptureIndexedEvidence(IPhysicsQueryLease lease, Vector3 minimum, Vector3 maximum,
        int maximumFeatures, PhysicsEvidenceVisitor visitor, StaticQueryExclusions? exclusions, QueryScratch scratch)
    {
        List<CollidableReference> candidates = scratch.Overlaps.Take();
        List<int> triangles = scratch.EvidenceTriangles.Take();
        try
        {
            candidates.Clear();
            var collector = new EvidenceOwners(candidates);
            _sim.BroadPhase.GetOverlaps(minimum, maximum, ref collector);
            candidates.Sort((a, b) => _reverseHandles[a.StaticHandle.Value].CompareTo(_reverseHandles[b.StaticHandle.Value]));
            int upper = 0;
            foreach (var candidate in candidates)
            {
                int id = _reverseHandles[candidate.StaticHandle.Value];
                _sim.Statics.GetDescription(candidate.StaticHandle, out var description);
                // A faulted or unpublished installation retains the original full-walk behavior.
                if (!_staticEvidence.TryGetValue(id, out var installed) ||
                    !TryGetInstalledSweepGeometry(candidate, out var geometry) ||
                    !ReferenceEquals(installed.Geometry, geometry) || installed.Pose.Position != description.Pose.Position ||
                    installed.Pose.Orientation != description.Pose.Orientation ||
                    !installed.Evidence.TryCountUpper(minimum, maximum, maximumFeatures - upper, out int required))
                    return CaptureFullEvidence(lease, minimum, maximum, maximumFeatures, visitor, exclusions);
                upper += required;
            }
            int features = 0;
            foreach (var candidate in candidates)
            {
                StaticEvidenceDiagnostics.Owner();
                int id = _reverseHandles[candidate.StaticHandle.Value];
                var installed = _staticEvidence[id];
                var status = EmitIndexedEvidence(new(id), installed.Evidence, exclusions?.ExcludesSweepStatic(id) ?? false,
                    minimum, maximum, maximumFeatures, ref features, visitor, triangles);
                if (status != PhysicsEvidenceStatus.Complete) return status;
            }
            lease.AssertCurrent();
            return PhysicsEvidenceStatus.Complete;
        }
        finally { scratch.EvidenceTriangles.Restore(triangles); scratch.Overlaps.Restore(candidates); }
    }

    struct EvidenceOwners(List<CollidableReference> candidates) : IBreakableForEach<CollidableReference>
    {
        public bool LoopBody(CollidableReference item)
        {
            if (item.Mobility == CollidableMobility.Static) candidates.Add(item);
            return true;
        }
    }
}
