using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.Locomotion.Contacts;

/// <summary>Query calls made through a <see cref="CountingQueryView"/>.</summary>
internal readonly record struct QueryCounts(int Sweeps, int Penetrations, int Raycasts, int Features)
{
    public static QueryCounts operator -(QueryCounts a, QueryCounts b) =>
        new(a.Sweeps - b.Sweeps, a.Penetrations - b.Penetrations, a.Raycasts - b.Raycasts,
            a.Features - b.Features);
}

/// <summary>A query view that forwards every call to a real selected view and counts the sweeps, penetrations,
/// raycasts and capsule-feature queries, support neighborhood queries included, and how many feature queries came back Unresolved. Leases come from the inner view and <see cref="SourceWorld"/> is the
/// inner view's, so foot support accepts it as that view. The caller owns the inner view.</summary>
internal sealed class CountingQueryView(IPhysicsWorldQueryView inner)
    : IPhysicsWorldQueryView, IPhysicsCapsuleFeatures, IPhysicsQueryLeaseSource
{
    int _sweeps, _penetrations, _raycasts, _features;

    internal QueryCounts Counts => new(_sweeps, _penetrations, _raycasts, _features);

    internal int UnresolvedFeatures { get; private set; }

    public IPhysicsWorld SourceWorld => inner.SourceWorld;
    public Vector3 Origin => inner.Origin;
    public bool CanRebase => inner.CanRebase;

    public IPhysicsQueryLease AcquireQueryReadLease() => ((IPhysicsQueryLeaseSource)inner).AcquireQueryReadLease();

    public bool SweepCapsule(CapsuleShape capsule, Pose pose, Vector3 direction, float maxDistance,
        out SweepHit hit, QueryFilter filter = default)
    {
        _sweeps++;
        return inner.SweepCapsule(capsule, pose, direction, maxDistance, out hit, filter);
    }

    public bool ComputePenetration(CapsuleShape capsule, Pose pose, out Vector3 mtv)
    {
        _penetrations++;
        return inner.ComputePenetration(capsule, pose, out mtv);
    }

    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit,
        QueryFilter filter = default)
    {
        _raycasts++;
        return inner.Raycast(origin, direction, maxDistance, out hit, filter);
    }

    public CapsuleFeatureResult QueryCapsuleFeature(IPhysicsQueryLease lease, StaticHandle target,
        CapsuleShape capsule, Pose pose, float maximumSeparationMetres, Span<CapsuleIncidentFace> faces,
        QueryFilter filter = default)
    {
        _features++;
        CapsuleFeatureResult result = ((IPhysicsCapsuleFeatures)inner).QueryCapsuleFeature(lease, target, capsule,
            pose, maximumSeparationMetres, faces, filter);
        if (result.Status == CapsuleFeatureStatus.Unresolved) UnresolvedFeatures++;
        return result;
    }

    public void AssertFeatureCurrent(in CapsuleFeatureResult result, IPhysicsQueryLease lease) =>
        ((IPhysicsCapsuleFeatures)inner).AssertFeatureCurrent(result, lease);

    public SupportNeighborhoodResult QuerySupportNeighborhood(IPhysicsQueryLease lease, CapsuleShape probe, Pose pose,
        float bandMetres, Span<SupportElement> elements, Span<ulong> joins, QueryFilter filter = default)
    {
        _features++;
        SupportNeighborhoodResult result = ((IPhysicsCapsuleFeatures)inner).QuerySupportNeighborhood(lease, probe,
            pose, bandMetres, elements, joins, filter);
        if (result.Status == CapsuleFeatureStatus.Unresolved) UnresolvedFeatures++;
        return result;
    }

    public void AssertNeighborhoodCurrent(in SupportNeighborhoodResult result, IPhysicsQueryLease lease) =>
        ((IPhysicsCapsuleFeatures)inner).AssertNeighborhoodCurrent(result, lease);

    public StaticHandle AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null) =>
        inner.AddStatic(shape, pose, material);
    public void RemoveStatic(StaticHandle handle) => inner.RemoveStatic(handle);
    public DynamicBodyHandle AddDynamic(PhysicsShape shape, Pose pose, DynamicBodyDescription body,
        PhysicsMaterial? material = null) => inner.AddDynamic(shape, pose, body, material);
    public void RemoveDynamic(DynamicBodyHandle handle) => inner.RemoveDynamic(handle);
    public Pose GetDynamicPose(DynamicBodyHandle handle) => inner.GetDynamicPose(handle);
    public void GetDynamicVelocity(DynamicBodyHandle handle, out Vector3 linear, out Vector3 angular) =>
        inner.GetDynamicVelocity(handle, out linear, out angular);
    public void SetDynamicVelocity(DynamicBodyHandle handle, Vector3 linear, Vector3 angular) =>
        inner.SetDynamicVelocity(handle, linear, angular);
    public bool IsAwake(DynamicBodyHandle handle) => inner.IsAwake(handle);
    public ConstraintHandle AddConstraint(in ConstraintDescription description) => inner.AddConstraint(description);
    public void RemoveConstraint(ConstraintHandle handle) => inner.RemoveConstraint(handle);
    public void SetConstraintTarget(ConstraintHandle handle, float target) => inner.SetConstraintTarget(handle, target);
    public void Step(float dt) => inner.Step(dt);
    public void Rebase(Vector3 newOrigin) => inner.Rebase(newOrigin);
    public IPhysicsWorldQueryView CreateQueryViewExcludingStatics(ReadOnlySpan<StaticHandle> excludedStatics) =>
        inner.CreateQueryViewExcludingStatics(excludedStatics);

    // The inner view belongs to the caller.
    public void Dispose() { }
}
