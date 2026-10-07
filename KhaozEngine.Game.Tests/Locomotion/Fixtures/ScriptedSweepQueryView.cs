using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.Locomotion.Fixtures;

// This decorator retains a real selected view and physical lease. It supplies no geometry certificate.
internal class SweepQueryView(IPhysicsWorldQueryView inner) : IPhysicsWorldQueryView, IPhysicsQueryLeaseSource
{
    public int LegacySweepCalls;
    public IPhysicsWorld SourceWorld => inner.SourceWorld;
    public Vector3 Origin => inner.Origin;
    public IPhysicsQueryLease AcquireQueryReadLease() =>
        ((IPhysicsQueryLeaseSource)inner).AcquireQueryReadLease();
    public bool Raycast(Vector3 origin, Vector3 direction, float distance, out RayHit hit, QueryFilter filter = default)
        => inner.Raycast(origin, direction, distance, out hit, filter);
    public bool SweepCapsule(CapsuleShape capsule, Pose pose, Vector3 direction, float distance,
        out SweepHit hit, QueryFilter filter = default)
    {
        LegacySweepCalls++;
        throw new InvalidOperationException("The explicit consumer must not use the legacy sweep.");
    }
    public bool ComputePenetration(CapsuleShape capsule, Pose pose, out Vector3 mtv)
        => inner.ComputePenetration(capsule, pose, out mtv);
    public Pose GetDynamicPose(DynamicBodyHandle handle) => inner.GetDynamicPose(handle);
    public void GetDynamicVelocity(DynamicBodyHandle handle, out Vector3 linear, out Vector3 angular)
        => inner.GetDynamicVelocity(handle, out linear, out angular);
    public bool IsAwake(DynamicBodyHandle handle) => inner.IsAwake(handle);
    public StaticHandle AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null) => throw new NotSupportedException();
    public void RemoveStatic(StaticHandle handle) => throw new NotSupportedException();
    public DynamicBodyHandle AddDynamic(PhysicsShape shape, Pose pose, DynamicBodyDescription body,
        PhysicsMaterial? material = null) => throw new NotSupportedException();
    public void RemoveDynamic(DynamicBodyHandle handle) => throw new NotSupportedException();
    public void SetDynamicVelocity(DynamicBodyHandle handle, Vector3 linear, Vector3 angular) => throw new NotSupportedException();
    public ConstraintHandle AddConstraint(in ConstraintDescription description) => throw new NotSupportedException();
    public void RemoveConstraint(ConstraintHandle handle) => throw new NotSupportedException();
    public void SetConstraintTarget(ConstraintHandle handle, float target) => throw new NotSupportedException();
    public void Step(float dt) => throw new NotSupportedException();
    public void Dispose() => inner.Dispose();
}

internal sealed class ScriptedSweepQueryView(IPhysicsWorldQueryView inner) : SweepQueryView(inner), IPhysicsCapsuleSweep
{
    public CapsuleSweepResult Result;
    public Action? OnSweep;
    public int Calls;
    public CapsuleShape? ObservedCapsule;
    public Pose ObservedPose;
    public Vector3 ObservedDisplacement;
    public QueryFilter ObservedFilter;

    public CapsuleSweepResult SweepCapsuleCertified(CapsuleShape capsule, Pose pose, Vector3 displacement,
        QueryFilter filter = default)
    {
        Calls++;
        ObservedCapsule = capsule;
        ObservedPose = pose;
        ObservedDisplacement = displacement;
        ObservedFilter = filter;
        OnSweep?.Invoke();
        return Result;
    }
}
