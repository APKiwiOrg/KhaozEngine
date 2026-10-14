using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using BepuPhysics;
using BepuPhysics.Collidables;
using KhaozEngine.Physics;
using StaticHandle = KhaozEngine.Physics.StaticHandle;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Successful local installation captures, published as immutable topology for concurrent readers.
/// No query result or refusal is retained. Owners, installation identities and static geometry versions must
/// all match before reuse. Concurrent misses for one key construct it once, and the only lock held is that
/// key's, across topology capture alone. Invalidation runs inside the owner's writer interval.</summary>
internal sealed class InstalledSupportGeometryCache
{
    readonly record struct Key(int Owner, TypedIndex Installation, long StaticGeometryVersion);
    readonly ConcurrentDictionary<Key, Slot> _entries = new();
    int _constructions;

    /// <summary>How many captures this cache has constructed.</summary>
    internal int Constructions => Volatile.Read(ref _constructions);

    internal CapsuleFeatureStatus GetOrCapture(Simulation simulation, StaticHandle owner, TypedIndex installation,
        long staticGeometryVersion, out InstalledSupportGeometry? geometry)
    {
        var key = new Key(owner.Value, installation, staticGeometryVersion);
        while (true)
        {
            if (!_entries.TryGetValue(key, out Slot? slot)) slot = _entries.GetOrAdd(key, new Slot());
            CapsuleFeatureStatus status = slot.Resolve(this, simulation, installation, out geometry, out bool constructed);
            if (status != CapsuleFeatureStatus.Complete)
            {
                _entries.TryRemove(new KeyValuePair<Key, Slot>(key, slot));
                return status;
            }
            // A capture this call made reads the live installation. A published one is checked against it.
            if (constructed || geometry!.Matches(simulation, installation)) return CapsuleFeatureStatus.Complete;
            _entries.TryRemove(new KeyValuePair<Key, Slot>(key, slot));
        }
    }

    internal void Remove(StaticHandle owner)
    {
        foreach (Key key in _entries.Keys)
            if (key.Owner == owner.Value) _entries.TryRemove(key, out _);
    }

    internal void Clear() => _entries.Clear();

    static CapsuleFeatureStatus Capture(Simulation simulation, TypedIndex installation,
        out InstalledSupportGeometry? geometry)
    {
        geometry = null;
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

    /// <summary>One key's capture. Publication is a volatile flag written after the immutable result.</summary>
    sealed class Slot
    {
        readonly Lock _gate = new();
        volatile bool _resolved;
        CapsuleFeatureStatus _status;
        InstalledSupportGeometry? _geometry;

        internal CapsuleFeatureStatus Resolve(InstalledSupportGeometryCache owner, Simulation simulation,
            TypedIndex installation, out InstalledSupportGeometry? geometry, out bool constructed)
        {
            constructed = false;
            if (!_resolved)
            {
                lock (_gate)
                {
                    if (!_resolved)
                    {
                        // A throwing capture publishes nothing, so a later reader captures again.
                        _status = Capture(simulation, installation, out _geometry);
                        Interlocked.Increment(ref owner._constructions);
                        _resolved = true;
                        constructed = true;
                    }
                }
            }
            geometry = _geometry;
            return _status;
        }
    }
}
