using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Trees;

namespace KhaozEngine.Physics.Bepu;

public sealed partial class BepuPhysicsWorld
{
    // Registration ids never repeat. Typed indices and native handles alone are not lifetimes.
    readonly Dictionary<int, InstalledSweepGeometry?> _installedSweepStatics = new();
    readonly Dictionary<int, InstalledSweepGeometry?> _installedSweepDynamics = new();

    void CaptureStaticSweepGeometry(int id, TypedIndex shape)
    {
        CapsuleSweepGeometry.TryCapture(_sim.Shapes, shape, out InstalledSweepGeometry? geometry);
        _installedSweepStatics.Add(id, geometry);
    }
    void CaptureDynamicSweepGeometry(int id, TypedIndex shape)
    {
        CapsuleSweepGeometry.TryCapture(_sim.Shapes, shape, out InstalledSweepGeometry? geometry);
        _installedSweepDynamics.Add(id, geometry);
    }

    bool TryGetInstalledSweepGeometry(CollidableReference reference, out InstalledSweepGeometry? geometry)
    {
        geometry = null;
        return reference.Mobility == CollidableMobility.Static
            ? _reverseHandles.TryGetValue(reference.StaticHandle.Value, out int id) && _installedSweepStatics.TryGetValue(id, out geometry)
            : _reverseDynamics.TryGetValue(reference.BodyHandle.Value, out id) && _installedSweepDynamics.TryGetValue(id, out geometry);
    }

    bool ObserveInstalledSweepBounds(CollidableReference reference, TypedIndex shape, RigidPose pose,
        Vector3 min, Vector3 max, out bool proved)
    {
        proved = false;
        if (!TryGetInstalledSweepGeometry(reference, out InstalledSweepGeometry? geometry)) return false;
        if (geometry is not null && !geometry.Matches(_sim.Shapes, shape)) return false;
        proved = IdentityBoxSweepBounds(shape, pose, min, max);
        if (geometry is null || proved || (!geometry.IsHull && geometry.Mesh is null && pose.Orientation == Quaternion.Identity)) return true;
        if (!InstalledSweepBounds.TryCreate(geometry, pose, out Vector3 requiredMin, out Vector3 requiredMax)) return geometry.Mesh is null;
        if (!ProveOrExpandSweepBounds(reference, geometry, pose, requiredMin, requiredMax)) return false;
        proved = true;
        return true;
    }

    bool ProveOrExpandSweepBounds(CollidableReference reference, InstalledSweepGeometry geometry, RigidPose pose,
        Vector3 requiredMin, Vector3 requiredMax)
    {
        if (!geometry.TryComposePose(pose, out RigidPose leafPose) ||
            !InstalledPoseOperator.TryCreate(leafPose, out InstalledPoseOperator map)) return false;
        bool active = reference.Mobility != CollidableMobility.Static && _sim.Bodies[reference.BodyHandle].Awake;
        int index = reference.Mobility == CollidableMobility.Static
            ? _sim.Statics[_sim.Statics.HandleToIndex[reference.StaticHandle.Value]].BroadPhaseIndex : _sim.Bodies[reference.BodyHandle].Collidable.BroadPhaseIndex;
        ref Tree tree = ref (active ? ref _sim.BroadPhase.ActiveTree : ref _sim.BroadPhase.StaticTree);
        if (!ReadSweepStoredBounds(tree, index, out Vector3 min, out Vector3 max) ||
            !SweepAncestorPath(tree, index, min, max, false)) return false;
        if (!ContainsSweepBounds(min, max, requiredMin, requiredMax))
        {
            min = Vector3.Min(min, requiredMin); max = Vector3.Max(max, requiredMax);
            // Bepu 2.4 BroadPhase.cs:111-124 delegates to Tree.UpdateBounds, which updates the leaf
            // and RefitForNodeBoundsChange walks its metanode parents to the root under this gate.
            if (active) _sim.BroadPhase.UpdateActiveBounds(index, min, max);
            else _sim.BroadPhase.UpdateStaticBounds(index, min, max);
        }
        if (!ReadSweepStoredBounds(tree, index, out min, out max) ||
            !ContainsSweepBounds(min, max, requiredMin, requiredMax) ||
            !SweepAncestorPath(tree, index, min, max, true)) return false;
        geometry.PublishPose(pose, leafPose, map);
        return true;
    }

    static bool ContainsSweepBounds(Vector3 min, Vector3 max, Vector3 requiredMin, Vector3 requiredMax) =>
        min.X <= requiredMin.X && min.Y <= requiredMin.Y && min.Z <= requiredMin.Z &&
        max.X >= requiredMax.X && max.Y >= requiredMax.Y && max.Z >= requiredMax.Z;

    static bool SweepAncestorPath(in Tree tree, int leaf, Vector3 min, Vector3 max, bool containment)
    {
        if (!tree.Metanodes.Allocated || tree.NodeCount > tree.Metanodes.Length) return false;
        int index = tree.Leaves[leaf].NodeIndex;
        for (int visited = 0; visited < CapsuleGeneralSweepPolicy.MaximumNodes; visited++)
        {
            if ((uint)index >= (uint)tree.NodeCount) return false;
            Metanode metadata = tree.Metanodes[index];
            if (metadata.Parent < 0) return index == 0;
            if ((uint)metadata.Parent >= (uint)tree.NodeCount || (uint)metadata.IndexInParent > 1) return false;
            ref readonly Node parent = ref tree.Nodes[metadata.Parent];
            NodeChild child = metadata.IndexInParent == 0 ? parent.A : parent.B;
            if (child.Index != index || !Finite(child.Min) || !Finite(child.Max) ||
                MaxAbs(child.Min) > MaximumSweepStoredCoordinate || MaxAbs(child.Max) > MaximumSweepStoredCoordinate ||
                child.Min.X > child.Max.X || child.Min.Y > child.Max.Y || child.Min.Z > child.Max.Z ||
                containment && !ContainsSweepBounds(child.Min, child.Max, min, max)) return false;
            min = child.Min; max = child.Max; index = metadata.Parent;
        }
        return false;
    }
}
