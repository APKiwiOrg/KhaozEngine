using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.Locomotion.Fixtures;

/// <summary>A deliberately value-equal decorator used to prove reference identity is enforced.</summary>
internal sealed class EquatablePhysicsView(IPhysicsWorldQueryView inner) :
    IPhysicsWorldQueryView, IPhysicsQueryLeaseSource, IPhysicsCapsuleContacts
{
    public IPhysicsWorld? ReportedSource;
    public IPhysicsWorld? LeaseSource;
    public IPhysicsWorld SourceWorld => ReportedSource ?? inner.SourceWorld;
    public Vector3 Origin => inner.Origin;
    public override bool Equals(object? obj) => obj is EquatablePhysicsView;
    public override int GetHashCode() => 1;

    public IPhysicsQueryLease AcquireQueryReadLease() =>
        new SourceLease(((IPhysicsQueryLeaseSource)inner).AcquireQueryReadLease(), LeaseSource ?? SourceWorld);

    sealed class SourceLease(IPhysicsQueryLease innerLease, IPhysicsWorld source) : IPhysicsQueryLease
    {
        public IPhysicsWorld SourceWorld => source;
        public Vector3 Origin => innerLease.Origin;
        public long GeometryGeneration => innerLease.GeometryGeneration;
        public void AssertCurrent() => innerLease.AssertCurrent();
        public void Dispose() => innerLease.Dispose();
    }

    public bool Raycast(Vector3 origin, Vector3 direction, float distance, out RayHit hit, QueryFilter filter = default)
        => inner.Raycast(origin, direction, distance, out hit, filter);
    public bool SweepCapsule(CapsuleShape capsule, Pose pose, Vector3 direction, float distance,
        out SweepHit hit, QueryFilter filter = default) => inner.SweepCapsule(capsule, pose, direction, distance, out hit, filter);
    public bool ComputePenetration(CapsuleShape capsule, Pose pose, out Vector3 mtv)
        => inner.ComputePenetration(capsule, pose, out mtv);
    public CapsuleContactResult QueryCapsuleContacts(CapsuleShape capsule, Pose pose, float margin,
        Span<CapsuleContact> destination, QueryFilter filter = default)
        => ((IPhysicsCapsuleContacts)inner).QueryCapsuleContacts(capsule, pose, margin, destination, filter);
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
    public void Dispose() { }
}
