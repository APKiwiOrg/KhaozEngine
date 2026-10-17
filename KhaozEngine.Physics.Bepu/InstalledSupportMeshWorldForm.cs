using System;
using System.Collections.Concurrent;
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

/// <summary>A bounded world-owned store of immutable mesh triangle forms. Concurrent map lookups take no lock.
/// Builds run outside every gate, so observers can wait for readers or reenter. Publication and FIFO eviction
/// share a short budget gate, and the first completed publication for a retained triangle wins.</summary>
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
    // Writes are serialised by the budget gate, so one write lock suffices, and reads stay lock-free. Sized to the
    // bound, so publication under the gate never resizes.
    readonly ConcurrentDictionary<int, Slot> _statics = new(concurrencyLevel: 1, capacity: StaticCapacity);
    readonly Queue<int> _owners = new(StaticCapacity);
    long _lockAcquisitions;
    int _triangleCount;

    internal long LockAcquisitions => Volatile.Read(ref _lockAcquisitions);
    internal int TriangleCount => Volatile.Read(ref _triangleCount);

    internal int TriangleCountFor(StaticHandle owner) =>
        _statics.TryGetValue(owner.Value, out Slot? slot) ? Volatile.Read(ref slot.Count) : 0;

    Lock.Scope EnterGate()
    {
        Interlocked.Increment(ref _lockAcquisitions);
        return _gate.EnterScope();
    }

    internal bool Matches(StaticHandle owner, TypedIndex shape, long version, in RigidPose pose)
    {
        Slot? slot = FindSlot(owner, shape, version);
        if (slot is null)
        {
            using (EnterGate()) slot = GetSlot(owner, shape, version, pose);
        }
        return slot.Matches(pose);
    }

    internal void Remove(StaticHandle owner)
    {
        using (EnterGate())
        {
            RemoveSlot(owner.Value);
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
        using (EnterGate())
        {
            _statics.Clear();
            _owners.Clear();
            Volatile.Write(ref _triangleCount, 0);
        }
    }

    internal CapsuleFeatureStatus Resolve(Simulation simulation, TypedIndex shape, in RigidPose pose,
        StaticHandle owner, long version, int triangle, in InstalledPoseOperator transform,
        SupportPolygonPool pool, out InstalledSupportMeshWorldForm? form)
    {
        Slot? slot = FindSlot(owner, shape, version);
        if (slot is not null && slot.Matches(pose) && slot.Triangles.TryGetValue(triangle, out form))
            return CapsuleFeatureStatus.Complete;
        // No capture, including an unobserved one, holds the budget gate. Concurrent cold misses may duplicate
        // arithmetic, just as observed installed-geometry misses do. A throwing capture publishes nothing.
        CapsuleFeatureStatus captured = Build(simulation, shape, pose, owner, triangle, transform, pool, out form);
        if (captured != CapsuleFeatureStatus.Complete) return captured;
        using (EnterGate())
        {
            slot = GetSlot(owner, shape, version, pose);
            if (slot.Matches(pose)) form = Publish(owner, slot, triangle, form!);
        }
        return captured;
    }

    InstalledSupportMeshWorldForm Publish(StaticHandle owner, Slot slot, int id, InstalledSupportMeshWorldForm form)
    {
        if (slot.Triangles.TryGetValue(id, out InstalledSupportMeshWorldForm? first)) return first;
        // Replacing a triangle in a full local slot needs no global space. Otherwise retire the oldest other
        // static until the new triangle fits. A current reader keeps its immutable face even after eviction.
        bool grows = slot.Count < PerStaticTriangleCapacity;
        if (grows)
            while (_triangleCount == GlobalTriangleCapacity)
            {
                int oldest = _owners.Dequeue();
                if (oldest == owner.Value) _owners.Enqueue(oldest);
                else RemoveSlot(oldest);
            }
        slot.Publish(id, form);
        if (grows) Volatile.Write(ref _triangleCount, _triangleCount + 1);
        return form;
    }

    Slot? FindSlot(StaticHandle owner, TypedIndex shape, long version) =>
        _statics.TryGetValue(owner.Value, out Slot? slot) &&
        slot.Shape.Packed == shape.Packed && slot.Version == version ? slot : null;

    // Only publication and eviction call these helpers, under the budget gate. Readers may retain an old slot
    // across replacement. Its identity and pose never change, and no evicted form or face is cleared or reused.
    Slot GetSlot(StaticHandle owner, TypedIndex shape, long version, in RigidPose pose)
    {
        if (_statics.TryGetValue(owner.Value, out Slot? slot))
        {
            if (slot.Shape.Packed == shape.Packed && slot.Version == version) return slot;
            Volatile.Write(ref _triangleCount, _triangleCount - slot.Count);
            return _statics[owner.Value] = new(shape, version, pose);
        }
        if (_owners.Count == StaticCapacity) RemoveSlot(_owners.Dequeue());
        _owners.Enqueue(owner.Value);
        slot = new(shape, version, pose);
        _statics.TryAdd(owner.Value, slot);
        return slot;
    }

    void RemoveSlot(int owner)
    {
        if (_statics.TryRemove(owner, out Slot? slot))
            Volatile.Write(ref _triangleCount, _triangleCount - slot.Count);
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
        // TryGetValue acquires the concurrent map's published node. The form and every array it owns are
        // immutable before insertion. FIFO mutation and Count updates belong exclusively to the budget gate.
        internal ConcurrentDictionary<int, InstalledSupportMeshWorldForm> Triangles { get; } =
            new(concurrencyLevel: 1, capacity: PerStaticTriangleCapacity);
        internal int Count;
        readonly Queue<int> _triangles = new(PerStaticTriangleCapacity);

        internal bool Matches(in RigidPose other) =>
            Same(pose.Position.X, other.Position.X) && Same(pose.Position.Y, other.Position.Y) &&
            Same(pose.Position.Z, other.Position.Z) && Same(pose.Orientation.X, other.Orientation.X) &&
            Same(pose.Orientation.Y, other.Orientation.Y) && Same(pose.Orientation.Z, other.Orientation.Z) &&
            Same(pose.Orientation.W, other.Orientation.W);

        static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

        internal void Publish(int id, InstalledSupportMeshWorldForm form)
        {
            bool grows = Count < PerStaticTriangleCapacity;
            if (!grows) Triangles.TryRemove(_triangles.Dequeue(), out _);
            _triangles.Enqueue(id);
            Triangles.TryAdd(id, form);
            if (grows) Volatile.Write(ref Count, Count + 1);
        }
    }
}
