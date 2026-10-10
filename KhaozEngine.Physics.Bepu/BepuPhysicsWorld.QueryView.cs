using System;
using System.Numerics;
using KhaozEngine.Physics;
using SeamConstraintHandle = KhaozEngine.Physics.ConstraintHandle;
using SeamStaticHandle = KhaozEngine.Physics.StaticHandle;

namespace KhaozEngine.Physics.Bepu;

public sealed partial class BepuPhysicsWorld
{
    /// <inheritdoc/>
    public IPhysicsWorldQueryView CreateQueryViewExcludingStatics(ReadOnlySpan<SeamStaticHandle> excludedStatics)
    {
        using QueryOperation scope = EnterQuery();
        SeamStaticHandle[] snapshot = excludedStatics.ToArray();
        foreach (SeamStaticHandle handle in snapshot)
        {
            if (!_handles.ContainsKey(handle.Value))
                throw new ArgumentException($"StaticHandle {handle.Value} is not a live static.", nameof(excludedStatics));
        }

        return new QueryView(this, new StaticQueryExclusions(snapshot, _reverseHandles));
    }

    private sealed class QueryView : IPhysicsWorldQueryView, IPhysicsQueryLeaseSource, IPhysicsCapsuleContacts,
        IPhysicsCapsuleSweep, IPhysicsCapsuleFeatures
    {
        private readonly BepuPhysicsWorld _owner;
        private readonly StaticQueryExclusions _exclusions;
        private bool _disposed;

        public QueryView(BepuPhysicsWorld owner, StaticQueryExclusions exclusions)
        {
            _owner = owner;
            _exclusions = exclusions;
        }

        public IPhysicsWorld SourceWorld => _owner;

        public IPhysicsQueryLease AcquireQueryReadLease()
        {
            ThrowIfDisposed();
            IPhysicsQueryLease lease = _owner.AcquireQueryReadLease();
            try
            {
                // Disposal may have won the gate after the first check but before acquisition.
                ThrowIfDisposed();
                return lease;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        public bool CanRebase => false;

        public CapsuleFeatureResult QueryCapsuleFeature(IPhysicsQueryLease lease, SeamStaticHandle target,
            CapsuleShape capsule, Pose pose, float maximumSeparationMetres, Span<CapsuleIncidentFace> faces,
            QueryFilter filter = default)
        {
            _owner.AuthenticateFeatureLease(lease);
            ThrowIfDisposed();
            return _owner.QueryCapsuleFeatureCore(this, lease, target, capsule, pose, maximumSeparationMetres,
                faces, filter, _exclusions);
        }

        public void AssertFeatureCurrent(in CapsuleFeatureResult result, IPhysicsQueryLease lease)
        {
            _owner.AuthenticateFeatureLease(lease);
            ThrowIfDisposed();
            _owner.AssertFeatureCurrentCore(this, result, lease);
        }

        public Vector3 Origin
        {
            get
            {
                using QueryOperation scope = _owner.EnterQuery();
                ThrowIfDisposed();
                return _owner.Origin;
            }
        }

        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit,
            QueryFilter filter = default)
        {
            using QueryOperation scope = _owner.EnterQuery();
            ThrowIfDisposed();
            return _owner.RaycastCore(origin, direction, maxDistance, out hit, filter, _exclusions);
        }

        public bool SweepCapsule(CapsuleShape capsule, Pose pose, Vector3 direction, float maxDistance,
            out SweepHit hit, QueryFilter filter = default)
        {
            using QueryOperation scope = _owner.EnterQuery();
            ThrowIfDisposed();
            return _owner.SweepCapsuleCore(capsule, pose, direction, maxDistance, out hit, filter, _exclusions);
        }

        public bool ComputePenetration(CapsuleShape capsule, Pose pose, out Vector3 mtv)
        {
            using QueryOperation scope = _owner.EnterQuery();
            ThrowIfDisposed();
            return _owner.ComputePenetrationCore(capsule, pose, out mtv, _exclusions);
        }

        public CapsuleContactResult QueryCapsuleContacts(CapsuleShape capsule, Pose pose, float maxSeparationMetres,
            Span<CapsuleContact> destination, QueryFilter filter = default)
        {
            using QueryOperation scope = _owner.EnterQuery();
            ThrowIfDisposed();
            return _owner.QueryCapsuleContactsCore(capsule, pose, maxSeparationMetres, destination, filter, _exclusions);
        }

        public CapsuleSweepResult SweepCapsuleCertified(CapsuleShape capsule, Pose pose, Vector3 displacement,
            QueryFilter filter = default)
        {
            using QueryOperation scope = _owner.EnterQuery();
            ThrowIfDisposed();
            return _owner.SweepCapsuleCertifiedCore(capsule, pose, displacement, filter, _exclusions);
        }

        public Pose GetDynamicPose(DynamicBodyHandle handle)
        {
            using QueryOperation scope = _owner.EnterQuery();
            ThrowIfDisposed();
            return _owner.GetDynamicPose(handle);
        }

        public void GetDynamicVelocity(DynamicBodyHandle handle, out Vector3 linear, out Vector3 angular)
        {
            using QueryOperation scope = _owner.EnterQuery();
            ThrowIfDisposed();
            _owner.GetDynamicVelocity(handle, out linear, out angular);
        }

        public bool IsAwake(DynamicBodyHandle handle)
        {
            using QueryOperation scope = _owner.EnterQuery();
            ThrowIfDisposed();
            return _owner.IsAwake(handle);
        }

        public SeamStaticHandle AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null)
            => throw MutationDenied();

        public void RemoveStatic(SeamStaticHandle handle) => throw MutationDenied();

        public DynamicBodyHandle AddDynamic(PhysicsShape shape, Pose pose, DynamicBodyDescription body,
            PhysicsMaterial? material = null) => throw MutationDenied();

        public void RemoveDynamic(DynamicBodyHandle handle) => throw MutationDenied();

        public void SetDynamicVelocity(DynamicBodyHandle handle, Vector3 linear, Vector3 angular)
            => throw MutationDenied();

        public SeamConstraintHandle AddConstraint(in ConstraintDescription description) => throw MutationDenied();

        public void RemoveConstraint(SeamConstraintHandle handle) => throw MutationDenied();

        public void SetConstraintTarget(SeamConstraintHandle handle, float target) => throw MutationDenied();

        public void Step(float dt) => throw MutationDenied();

        public void Rebase(Vector3 newOrigin) => throw MutationDenied();

        public IPhysicsWorldQueryView CreateQueryViewExcludingStatics(ReadOnlySpan<SeamStaticHandle> excludedStatics)
            => throw MutationDenied();

        public void Dispose()
        {
            if (_disposed) return;
            using QueryOperation scope = _owner.EnterMutation(allowDisposed: true, changesGeometry: false);
            _disposed = true;
        }

        private NotSupportedException MutationDenied()
        {
            ThrowIfDisposed();
            return new NotSupportedException("Physics query views do not support mutations.");
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(QueryView));
        }
    }
}
