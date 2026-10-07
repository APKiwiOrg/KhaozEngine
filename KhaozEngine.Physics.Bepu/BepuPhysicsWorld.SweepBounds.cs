using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Trees;

namespace KhaozEngine.Physics.Bepu;

public sealed partial class BepuPhysicsWorld
{
    readonly Dictionary<int, bool> _sweepStaticBounds = new();
    readonly Dictionary<int, bool> _sweepDynamicBounds = new();
    readonly HashSet<int> _unprovedSweepStatics = new();
    readonly HashSet<int> _unprovedSweepDynamics = new();

    bool ObserveStaticSweepBounds(int id)
    {
        if (!_handles.TryGetValue(id, out var entry) ||
            !_reverseHandles.TryGetValue(entry.Handle.Value, out int reverse) || reverse != id ||
            (uint)entry.Handle.Value >= (uint)_sim.Statics.HandleToIndex.Length) return false;
        int index = _sim.Statics.HandleToIndex[entry.Handle.Value];
        if ((uint)index >= (uint)_sim.Statics.Count || _sim.Statics.IndexToHandle[index].Value != entry.Handle.Value)
            return false;
        ref var body = ref _sim.Statics[index];
        if (!SameSweepShape(body.Shape, entry.Shape) ||
            !ReadSweepStoredBounds(_sim.BroadPhase.StaticTree, body.BroadPhaseIndex, out Vector3 min, out Vector3 max))
            return false;
        bool proved = IdentityBoxSweepBounds(body.Shape, body.Pose, min, max);
        SetSweepBoundStatus(_sweepStaticBounds, _unprovedSweepStatics, id, proved);
        return true;
    }

    bool ObserveDynamicSweepBounds(int id)
    {
        if (!_dynamics.TryGetValue(id, out var entry) ||
            !_reverseDynamics.TryGetValue(entry.Handle.Value, out int reverse) || reverse != id ||
            !_sim.Bodies.BodyExists(entry.Handle)) return false;
        var body = _sim.Bodies.GetBodyReference(entry.Handle);
        if (!SameSweepShape(body.Collidable.Shape, entry.Shape)) return false;
        bool stored = body.Awake
            ? ReadSweepStoredBounds(_sim.BroadPhase.ActiveTree, body.Collidable.BroadPhaseIndex, out Vector3 min, out Vector3 max)
            : ReadSweepStoredBounds(_sim.BroadPhase.StaticTree, body.Collidable.BroadPhaseIndex, out min, out max);
        if (!stored) return false;
        bool proved = IdentityBoxSweepBounds(body.Collidable.Shape, body.Pose, min, max);
        SetSweepBoundStatus(_sweepDynamicBounds, _unprovedSweepDynamics, id, proved);
        return true;
    }

    bool IdentityBoxSweepBounds(TypedIndex shape, RigidPose pose, Vector3 min, Vector3 max)
    {
        if (!CapsuleSweepGeometry.TryReadIdentityBox(_sim.Shapes, shape, pose, out Box box)) return false;
        // The pinned identity-box calculation has exact local half extents followed by one binary32
        // translation. Conservative expansion is allowed. An outward query aperture cannot miss a
        // touching face solely because of that final monotonic rounding.
        Vector3 half = new(box.HalfWidth, box.HalfHeight, box.HalfLength);
        Vector3 expectedMin = pose.Position - half, expectedMax = pose.Position + half;
        return Finite(expectedMin) && Finite(expectedMax) &&
            min.X <= expectedMin.X && min.Y <= expectedMin.Y && min.Z <= expectedMin.Z &&
            max.X >= expectedMax.X && max.Y >= expectedMax.Y && max.Z >= expectedMax.Z;
    }

    static bool ReadSweepStoredBounds(in Tree tree, int leafIndex, out Vector3 min, out Vector3 max)
    {
        min = max = default;
        if (tree.LeafCount < 1 || !tree.Leaves.Allocated || !tree.Nodes.Allocated ||
            (uint)leafIndex >= (uint)tree.LeafCount || tree.LeafCount > tree.Leaves.Length ||
            tree.NodeCount < 1 || tree.NodeCount > tree.Nodes.Length) return false;
        Leaf leaf = tree.Leaves[leafIndex];
        if ((uint)leaf.NodeIndex >= (uint)tree.NodeCount || (uint)leaf.ChildIndex > 1) return false;
        ref readonly Node node = ref tree.Nodes[leaf.NodeIndex];
        NodeChild child = leaf.ChildIndex == 0 ? node.A : node.B;
        if (child.Index != Tree.Encode(leafIndex) || !Finite(child.Min) || !Finite(child.Max) ||
            child.Min.X > child.Max.X || child.Min.Y > child.Max.Y || child.Min.Z > child.Max.Z) return false;
        min = child.Min;
        max = child.Max;
        return true;
    }

    static bool SameSweepShape(TypedIndex a, TypedIndex b) =>
        a.Exists && b.Exists && a.Type == b.Type && a.Index == b.Index;

    static void SetSweepBoundStatus(Dictionary<int, bool> records, HashSet<int> unproved, int id, bool proved)
    {
        records[id] = proved;
        if (proved) unproved.Remove(id);
        else unproved.Add(id);
    }

    bool SweepRecordCountsMatch() =>
        _sim.Statics.Count == _handles.Count && _reverseHandles.Count == _handles.Count &&
        _reverseDynamics.Count == _dynamics.Count && _sweepStaticBounds.Count == _handles.Count &&
        _sweepDynamicBounds.Count == _dynamics.Count && _sim.BroadPhase.ActiveTree.LeafCount >= 0 &&
        _sim.BroadPhase.StaticTree.LeafCount >= 0 &&
        (long)_sim.BroadPhase.ActiveTree.LeafCount + _sim.BroadPhase.StaticTree.LeafCount ==
        (long)_handles.Count + _dynamics.Count;

    bool SelectedSweepBoundsProved(QueryMobility mobility, StaticQueryExclusions? exclusions)
    {
        if (mobility != QueryMobility.Statics && _unprovedSweepDynamics.Count != 0) return false;
        if (mobility == QueryMobility.Dynamics) return true;
        if (_unprovedSweepStatics.Count > MaximumSweepCandidates) return false;
        foreach (int id in _unprovedSweepStatics)
        {
            if (!_handles.TryGetValue(id, out var entry) || exclusions is null ||
                exclusions.Allows(new CollidableReference(entry.Handle))) return false;
        }
        return true;
    }
}
