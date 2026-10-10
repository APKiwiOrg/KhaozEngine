using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.MapDoc.Physics;

/// <summary>A test <see cref="IPhysicsWorld"/> for registration faults. It counts live statics, records removals in
/// order and reports the given origin. It throws <see cref="InvalidOperationException"/> on the
/// <paramref name="failOnAdd"/>th <see cref="AddStatic"/> call and, once, on the <paramref name="failOnRemove"/>th
/// <see cref="RemoveStatic"/> call, leaving that static live. A count of 0 never fails. Only registration and disposal
/// are supported.</summary>
internal sealed class NativeRegistrationFaultWorld(int failOnAdd = 0, Vector3 origin = default, int failOnRemove = 0)
    : IPhysicsWorld
{
    readonly HashSet<int> _live = new();
    readonly List<int> _removed = new();
    int _adds, _removes;

    internal int LiveStaticCount => _live.Count;

    /// <summary>Every successfully removed handle value, in removal order.</summary>
    internal IReadOnlyList<int> Removed => _removed;

    public Vector3 Origin { get; } = origin;

    public StaticHandle AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null)
    {
        if (++_adds == failOnAdd) throw new InvalidOperationException("Fault world refused static add " + _adds + ".");
        _live.Add(_adds);
        return new StaticHandle(_adds);
    }

    public void RemoveStatic(StaticHandle handle)
    {
        if (++_removes == failOnRemove)
            throw new InvalidOperationException("Fault world refused static removal " + _removes + ".");
        if (!_live.Remove(handle.Value)) throw new InvalidOperationException("Fault world has no static " + handle.Value + ".");
        _removed.Add(handle.Value);
    }

    public void Dispose() { }

    public DynamicBodyHandle AddDynamic(PhysicsShape shape, Pose pose, DynamicBodyDescription body,
        PhysicsMaterial? material = null) => throw new NotSupportedException();

    public void RemoveDynamic(DynamicBodyHandle handle) => throw new NotSupportedException();

    public Pose GetDynamicPose(DynamicBodyHandle handle) => throw new NotSupportedException();

    public void GetDynamicVelocity(DynamicBodyHandle handle, out Vector3 linear, out Vector3 angular) =>
        throw new NotSupportedException();

    public void SetDynamicVelocity(DynamicBodyHandle handle, Vector3 linear, Vector3 angular) =>
        throw new NotSupportedException();

    public bool IsAwake(DynamicBodyHandle handle) => throw new NotSupportedException();

    public ConstraintHandle AddConstraint(in ConstraintDescription description) => throw new NotSupportedException();

    public void RemoveConstraint(ConstraintHandle handle) => throw new NotSupportedException();

    public void SetConstraintTarget(ConstraintHandle handle, float target) => throw new NotSupportedException();

    public void Step(float dt) => throw new NotSupportedException();

    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit, QueryFilter filter = default) =>
        throw new NotSupportedException();

    public bool SweepCapsule(CapsuleShape capsule, Pose pose, Vector3 direction, float maxDistance, out SweepHit hit,
        QueryFilter filter = default) => throw new NotSupportedException();

    public bool ComputePenetration(CapsuleShape capsule, Pose pose, out Vector3 mtv) => throw new NotSupportedException();
}
