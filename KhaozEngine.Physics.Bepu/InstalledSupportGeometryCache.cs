using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using KhaozEngine.Physics;
using StaticHandle = KhaozEngine.Physics.StaticHandle;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Successful local installation captures. No query result or refusal is retained.
/// Owners, installation identities and static geometry versions must all match before reuse.</summary>
internal sealed class InstalledSupportGeometryCache
{
    readonly record struct Entry(TypedIndex Installation, long StaticGeometryVersion, InstalledSupportGeometry Geometry);
    readonly Dictionary<int, Entry> _entries = [];

    internal CapsuleFeatureStatus GetOrCapture(Simulation simulation, StaticHandle owner, TypedIndex installation,
        long staticGeometryVersion, out InstalledSupportGeometry? geometry)
    {
        geometry = null;
        if (_entries.TryGetValue(owner.Value, out Entry entry) && entry.Installation == installation &&
            entry.StaticGeometryVersion == staticGeometryVersion && entry.Geometry.Matches(simulation, installation))
        {
            geometry = entry.Geometry;
            return CapsuleFeatureStatus.Complete;
        }
        _entries.Remove(owner.Value);
        var leaves = new List<InstalledSupportLeaf>();
        CapsuleFeatureStatus status;
        if (installation.Type == default(Compound).TypeId)
        {
            ref Compound compound = ref simulation.Shapes.GetShape<Compound>(installation.Index);
            if (compound.Children.Length == 0) return CapsuleFeatureStatus.Unsupported;
            for (int i = 0; i < compound.Children.Length; i++)
            {
                ref var child = ref compound.Children[i];
                if (!CapsuleFeatureGeometry.ProveInstalledPose(child.LocalPose)) return CapsuleFeatureStatus.Unsupported;
                status = CaptureLeaf(simulation, child.ShapeIndex, child.LocalPose, i, leaves);
                if (status != CapsuleFeatureStatus.Complete) return status;
            }
        }
        else
        {
            status = CaptureLeaf(simulation, installation, new RigidPose(Vector3.Zero, Quaternion.Identity), 0, leaves);
            if (status != CapsuleFeatureStatus.Complete) return status;
        }
        geometry = new(leaves);
        _entries.Add(owner.Value, new(installation, staticGeometryVersion, geometry));
        return CapsuleFeatureStatus.Complete;
    }

    static CapsuleFeatureStatus CaptureLeaf(Simulation simulation, TypedIndex installation, in RigidPose local,
        int id, List<InstalledSupportLeaf> leaves)
    {
        RigidPose identity = new(Vector3.Zero, Quaternion.Identity);
        Pose localPose = new(local.Position, local.Orientation);
        if (installation.Type == default(Box).TypeId || installation.Type == default(ConvexHull).TypeId)
        {
            CapsuleFeatureStatus status = CapsuleFeatureGeometry.CaptureLeaf(simulation, installation, identity,
                local, id, null, out var captured, topologyOnly: true);
            if (status != CapsuleFeatureStatus.Complete) return status;
            leaves.Add(new(id, localPose, captured!.LocalVertices, captured.Faces, null, 0, 0, installation));
            return CapsuleFeatureStatus.Complete;
        }
        if (installation.Type != default(Sphere).TypeId && installation.Type != default(Capsule).TypeId &&
            installation.Type != default(Cylinder).TypeId) return CapsuleFeatureStatus.Unsupported;
        var curved = new List<SupportCurvedLeaf>();
        CapsuleFeatureStatus curvedStatus = SupportNeighborhoodCurved.Leaf(simulation, installation, identity, id, curved);
        if (curvedStatus != CapsuleFeatureStatus.Complete) return curvedStatus;
        SupportCurvedLeaf leaf = curved[0];
        leaves.Add(new(id, localPose, [], [], leaf.Shape, leaf.Radius, leaf.HalfLength, installation));
        return CapsuleFeatureStatus.Complete;
    }

    internal void Remove(StaticHandle owner) => _entries.Remove(owner.Value);
    internal void Clear() => _entries.Clear();
}
