using System;
using System.Collections.Generic;
using System.Threading;
using BepuPhysics;
using BepuPhysics.Collidables;
using KhaozEngine.Physics;
using StaticHandle = KhaozEngine.Physics.StaticHandle;

namespace KhaozEngine.Physics.Bepu;

/// <summary>One triangle's immutable pose-applied facts. A null face is an exactly degenerate triangle or a
/// normal with no certifiable area, never a query refusal. Query bounds and operands are not retained.</summary>
internal sealed record InstalledSupportMeshWorldForm(InstalledSupportWorldFace? Face);

/// <summary>A bounded world-owned store of immutable mesh triangle forms. All dictionary access and publication
/// use one gate. Unobserved construction holds it, while observed construction runs outside it, so foreign
/// observer code can wait for readers or reenter. The first completed publication for a triangle wins.</summary>
internal sealed class InstalledSupportMeshWorldCache
{
    // Two maximum-size (256-member) footprints fit per static, including neighbours selected by the tree.
    // Up to 256 small statics fit a maximum-size footprint without slot churn. The independent 8192-triangle
    // global budget holds sixteen full static neighbourhoods, or many smaller ones, regardless of terrain size.
    // FIFO replacement bounds both dictionaries. Eviction affects capture work alone, never membership or order.
    internal const int PerStaticTriangleCapacity = 512;
    internal const int StaticCapacity = 256;
    internal const int GlobalTriangleCapacity = 8192;

    readonly Lock _gate = new();
    readonly Dictionary<int, Slot> _statics = [];
    readonly Queue<int> _owners = new(StaticCapacity);

    internal int TriangleCount
    {
        get
        {
            lock (_gate)
            {
                return CountTriangles();
            }
        }
    }

    internal int TriangleCountFor(StaticHandle owner)
    {
        lock (_gate) return _statics.TryGetValue(owner.Value, out Slot? slot) ? slot.Triangles.Count : 0;
    }

    internal bool Matches(StaticHandle owner, TypedIndex shape, long version, in RigidPose pose)
    {
        lock (_gate) return GetSlot(owner, shape, version, pose).Matches(pose);
    }

    internal void Remove(StaticHandle owner)
    {
        lock (_gate)
        {
            _statics.Remove(owner.Value);
            // Keep the FIFO free of tombstones, including repeated removal and handle reuse.
            int count = _owners.Count;
            for (int i = 0; i < count; i++)
            {
                int id = _owners.Dequeue();
                if (id != owner.Value) _owners.Enqueue(id);
            }
        }
    }

    internal void Clear()
    {
        lock (_gate) { _statics.Clear(); _owners.Clear(); }
    }

    internal CapsuleFeatureStatus Resolve(Simulation simulation, TypedIndex shape, in RigidPose pose,
        StaticHandle owner, long version, int triangle, in InstalledPoseOperator transform,
        SupportPolygonPool pool, out InstalledSupportMeshWorldForm? form)
    {
        bool observed = ContactAllocationDiagnostics.Observed;
        lock (_gate)
        {
            Slot slot = GetSlot(owner, shape, version, pose);
            if (slot.Matches(pose) && slot.Triangles.TryGetValue(triangle, out form))
                return CapsuleFeatureStatus.Complete;
            if (!observed)
            {
                CapsuleFeatureStatus status = Build(simulation, shape, pose, owner, triangle, transform, pool, out form);
                if (status == CapsuleFeatureStatus.Complete && slot.Matches(pose))
                    form = Publish(owner, slot, triangle, form!);
                return status;
            }
        }
        CapsuleFeatureStatus captured = Build(simulation, shape, pose, owner, triangle, transform, pool, out form);
        if (captured != CapsuleFeatureStatus.Complete) return captured;
        lock (_gate)
        {
            Slot slot = GetSlot(owner, shape, version, pose);
            if (slot.Matches(pose)) form = Publish(owner, slot, triangle, form!);
        }
        return captured;
    }

    int CountTriangles()
    {
        int count = 0;
        foreach (Slot slot in _statics.Values) count += slot.Triangles.Count;
        return count;
    }

    InstalledSupportMeshWorldForm Publish(StaticHandle owner, Slot slot, int id, InstalledSupportMeshWorldForm form)
    {
        if (slot.Triangles.TryGetValue(id, out InstalledSupportMeshWorldForm? first)) return first;
        // Replacing a triangle in a full local slot needs no global space. Otherwise retire the oldest other
        // static until the new triangle fits. A current reader keeps its immutable face even after eviction.
        if (slot.Triangles.Count < PerStaticTriangleCapacity)
            while (CountTriangles() == GlobalTriangleCapacity)
            {
                int oldest = _owners.Dequeue();
                if (oldest == owner.Value) _owners.Enqueue(oldest);
                else _statics.Remove(oldest);
            }
        return slot.Publish(id, form);
    }

    Slot GetSlot(StaticHandle owner, TypedIndex shape, long version, in RigidPose pose)
    {
        if (_statics.TryGetValue(owner.Value, out Slot? slot))
        {
            if (slot.Shape.Packed == shape.Packed && slot.Version == version) return slot;
            return _statics[owner.Value] = new(shape, version, pose);
        }
        if (_statics.Count == StaticCapacity) _statics.Remove(_owners.Dequeue());
        _owners.Enqueue(owner.Value);
        slot = new(shape, version, pose);
        _statics.Add(owner.Value, slot);
        return slot;
    }

    static CapsuleFeatureStatus Build(Simulation simulation, TypedIndex shape, in RigidPose pose,
        StaticHandle owner, int triangle, in InstalledPoseOperator transform, SupportPolygonPool pool,
        out InstalledSupportMeshWorldForm? form)
    {
        using var phase = ContactAllocationDiagnostics.Begin(ContactAllocationPhase.SupportMaterialization);
        SupportQueryDiagnostics.Record(SupportQueryCounter.MeshTriangleCaptures);
        SupportQueryDiagnostics.ThreadState counting = SupportQueryDiagnostics.Exchange(
            SupportQueryDiagnostics.ThreadState.Fresh(false));
        CapsuleFeatureStatus status;
        try
        {
            status = SupportNeighborhoodMesh.CaptureTriangle(simulation, shape, pose, owner, triangle,
                transform, pool, exactZeroProducts: true, out SupportPolygon? polygon);
            form = status == CapsuleFeatureStatus.Complete
                ? new(polygon is null ? null : new InstalledSupportWorldFace(polygon, [])) : null;
            if (polygon is not null) pool.ReturnLast();
        }
        finally { SupportQueryDiagnostics.Exchange(counting); }
        if (status == CapsuleFeatureStatus.Complete) SupportQueryDiagnostics.Record(SupportQueryCounter.WorldFormBuilds);
        return status;
    }

    sealed class Slot(TypedIndex shape, long version, RigidPose pose)
    {
        internal TypedIndex Shape { get; } = shape;
        internal long Version { get; } = version;
        internal Dictionary<int, InstalledSupportMeshWorldForm> Triangles { get; } = [];
        readonly Queue<int> _triangles = new(PerStaticTriangleCapacity);

        internal bool Matches(in RigidPose other) =>
            Same(pose.Position.X, other.Position.X) && Same(pose.Position.Y, other.Position.Y) &&
            Same(pose.Position.Z, other.Position.Z) && Same(pose.Orientation.X, other.Orientation.X) &&
            Same(pose.Orientation.Y, other.Orientation.Y) && Same(pose.Orientation.Z, other.Orientation.Z) &&
            Same(pose.Orientation.W, other.Orientation.W);

        static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

        internal InstalledSupportMeshWorldForm Publish(int id, InstalledSupportMeshWorldForm form)
        {
            if (Triangles.TryGetValue(id, out InstalledSupportMeshWorldForm? first)) return first;
            if (Triangles.Count == PerStaticTriangleCapacity) Triangles.Remove(_triangles.Dequeue());
            _triangles.Enqueue(id);
            Triangles.Add(id, form);
            return form;
        }
    }
}
