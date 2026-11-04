using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Movement;

/// <summary>Observes the actual rays of the unchanged column probe. Only its accepted output bits bind raw rows.</summary>
internal sealed class PhysicsColumnEvidenceProbe(IPhysicsWorld world, IPhysicsQueryLease lease) : IPhysicsWorld
{
    internal readonly record struct Hit(Vector3 Origin, RayHit Result, int Leaf);
    internal List<Hit> Hits { get; } = [];
    public bool Raycast(Vector3 origin, Vector3 direction, float distance, out RayHit hit, QueryFilter filter = default)
    {
        int leaf = -1;
        bool found;
        if (world is IPhysicsStaticEvidenceRaycast evidence && filter.Mobility == QueryMobility.Statics)
        {
            var status = evidence.RaycastStaticEvidence(lease, origin, direction, distance, out var selected, out leaf);
            if (status != PhysicsEvidenceStatus.Complete)
                throw new PhysicsNavDescentEvidenceException(PhysicsNavDescentEvidenceReason.BackendRefused, status);
            hit = selected.GetValueOrDefault();
            found = selected.HasValue;
        }
        else found = world.Raycast(origin, direction, distance, out hit, filter);
        if (found) Hits.Add(new(origin, hit, leaf));
        return found;
    }
    public Vector3 Origin => world.Origin;
    public void Dispose() { }
    public StaticHandle AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null) => throw new NotSupportedException();
    public void RemoveStatic(StaticHandle handle) => throw new NotSupportedException();
    public DynamicBodyHandle AddDynamic(PhysicsShape shape, Pose pose, DynamicBodyDescription body, PhysicsMaterial? material = null) => throw new NotSupportedException();
    public void RemoveDynamic(DynamicBodyHandle handle) => throw new NotSupportedException();
    public Pose GetDynamicPose(DynamicBodyHandle handle) => throw new NotSupportedException();
    public void GetDynamicVelocity(DynamicBodyHandle handle, out Vector3 linear, out Vector3 angular) => throw new NotSupportedException();
    public void SetDynamicVelocity(DynamicBodyHandle handle, Vector3 linear, Vector3 angular) => throw new NotSupportedException();
    public bool IsAwake(DynamicBodyHandle handle) => throw new NotSupportedException();
    public ConstraintHandle AddConstraint(in ConstraintDescription description) => throw new NotSupportedException();
    public void RemoveConstraint(ConstraintHandle handle) => throw new NotSupportedException();
    public void SetConstraintTarget(ConstraintHandle handle, float target) => throw new NotSupportedException();
    public void Step(float dt) => throw new NotSupportedException();
    public bool SweepCapsule(CapsuleShape capsule, Pose pose, Vector3 direction, float maxDistance, out SweepHit hit, QueryFilter filter = default) => throw new NotSupportedException();
    public bool ComputePenetration(CapsuleShape capsule, Pose pose, out Vector3 mtv) => throw new NotSupportedException();
}
