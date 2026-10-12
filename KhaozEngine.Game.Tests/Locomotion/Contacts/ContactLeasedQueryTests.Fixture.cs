using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public partial class ContactLeasedQueryTests
{
    sealed class QueryScene : IDisposable
    {
        readonly BepuPhysicsWorld? ownedWorld;
        public ContactQueryView View { get; }
        public EnvironmentAcquisitionFixture Environment { get; }
        public MovementQueryLease Lease { get; }
        public QueryScene(Func<float, float>? height = null, Vector3? normal = null,
            Vector3? scopeMax = null, float maxRise = 20, float maxDrop = 20, Vector3? scopeMin = null) :
            this(null, height, normal, scopeMax, maxRise, maxDrop, scopeMin)
        { }

        public QueryScene(FootSupportScene? geometry, Func<float, float>? height = null, Vector3? normal = null,
            Vector3? scopeMax = null, float maxRise = 20, float maxDrop = 20, Vector3? scopeMin = null)
        {
            BepuPhysicsWorld world = geometry?.World ?? (ownedWorld = new(Vector3.Zero));
            View = new(world.CreateQueryViewExcludingStatics([]));
            var identity = new MovementQueryIdentity("closure", 1, "scope");
            var frame = new MovementFrameDescriptor(WorldFrame.Origin, Vector3.Zero, 1);
            var scope = new MovementQueryScope(scopeMin ?? new(-100), scopeMax ?? new(100), maxRise, maxDrop, Room, identity, frame);
            Environment = new(View, scope) { ContactSupportEnabled = true };
            Environment.Point = new(MovementAvailability.Known, Room, null, false, 0, null);
            Environment.OnAnalyticGround = (in MovementSupportRequest request, out MovementAnalyticGround? ground) =>
            {
                Vector2 axis = new(request.Body.Centre.X, request.Body.Centre.Z);
                ground = height is null ? null : new(axis, height(axis.X), normal ?? Vector3.UnitY,
                    new("world", "floor"), Room, null, identity);
                return MovementAvailability.Known;
            };
            Environment.OnContactSupport = (in MovementContactSupportRequest request, out MovementContactSupportBinding binding) =>
            {
                string name = request.Static.HasValue ? "bank" : "floor";
                binding = new(new("world", name), Room, null, identity);
                return MovementAvailability.Known;
            };
            Environment.OnCoverage = (in MovementMediumSweepQuery query, Span<MovementCoverageSpan> spans,
                Span<MovementDomainContact> contacts) =>
            {
                spans[0] = new(0, 1, 0, 0, true);
                return new(MovementAvailability.Known, 1, 1, 0, 0, 0, identity);
            };
            var acquired = Environment.Acquire();
            Assert.Equal(MovementAvailability.Known, acquired.Status);
            Lease = Assert.IsType<MovementQueryLease>(acquired.Lease);
            Assert.Equal(1, View.Acquisitions);
        }

        public LeasedContactQueryContext Queries(float offset = Skin) => new(Lease, Tuning,
            new MovementSelection(Room, null, Lease.Identity), new MovementTransitionContext(Room, Vector3.Zero), offset);
        public void Dispose() { Lease.Dispose(); View.Dispose(); ownedWorld?.Dispose(); }
    }

    sealed class ContactQueryView(IPhysicsWorldQueryView inner) : IPhysicsWorldQueryView,
        IPhysicsQueryLeaseSource, IPhysicsSupportNeighborhood, IPhysicsSupportFootprint, IPhysicsCapsuleSweep, IPhysicsCapsuleContacts
    {
        public int Calls, Acquisitions, FootprintCalls;
        // FailFootprintAt expires the interval at that call. RefuseFootprintFrom returns an atomic capacity
        // refusal from that call on, with both output spans untouched.
        public int FailFootprintAt, RefuseFootprintFrom;
        public bool FailPushedSupport, RefusePushedSupport;
        public Vector3 PushMtv;
        public SupportFootprintRequest? PushedSupportRequest;
        bool _supportAfterPush;
        public IPhysicsQueryLease? AcquiredLease, FootprintLease;
        public CapsuleShape? RawShape;
        public IPhysicsWorld SourceWorld => inner.SourceWorld;
        public Vector3 Origin => inner.Origin;
        public IPhysicsQueryLease AcquireQueryReadLease()
        { Acquisitions++; return AcquiredLease = ((IPhysicsQueryLeaseSource)inner).AcquireQueryReadLease(); }
        public bool SweepCapsule(CapsuleShape shape, Pose pose, Vector3 direction, float distance,
            out SweepHit hit, QueryFilter filter = default)
        { Calls++; RawShape = shape; return inner.SweepCapsule(shape, pose, direction, distance, out hit, filter); }
        public bool ComputePenetration(CapsuleShape shape, Pose pose, out Vector3 mtv)
        {
            Calls++; RawShape = shape;
            bool overlapping = inner.ComputePenetration(shape, pose, out mtv);
            if (overlapping && mtv != Vector3.Zero) { PushMtv = mtv; _supportAfterPush = true; }
            return overlapping;
        }
        public CapsuleSweepResult SweepCapsuleCertified(CapsuleShape shape, Pose pose, Vector3 delta,
            QueryFilter filter = default)
        { Calls++; return ((IPhysicsCapsuleSweep)inner).SweepCapsuleCertified(shape, pose, delta, filter); }
        public CapsuleContactResult QueryCapsuleContacts(CapsuleShape shape, Pose pose, float margin,
            Span<CapsuleContact> contacts, QueryFilter filter = default)
        { Calls++; return ((IPhysicsCapsuleContacts)inner).QueryCapsuleContacts(shape, pose, margin, contacts, filter); }
        public SupportNeighborhoodResult QuerySupportNeighborhood(IPhysicsQueryLease lease, CapsuleShape probe,
            Pose pose, float band, Span<SupportElement> elements, Span<ulong> joins, QueryFilter filter = default)
        { Calls++; return ((IPhysicsSupportNeighborhood)inner).QuerySupportNeighborhood(lease, probe, pose, band, elements, joins, filter); }
        public SupportFootprintResult QuerySupportFootprint(IPhysicsQueryLease lease, in SupportFootprintRequest request,
            Span<SupportFootprintMember> members, Span<ulong> joins, QueryFilter filter = default)
        {
            Calls++;
            FootprintCalls++;
            FootprintLease ??= lease;
            Assert.Same(AcquiredLease, lease);
            Assert.Same(FootprintLease, lease);
            if (_supportAfterPush)
            {
                _supportAfterPush = false;
                PushedSupportRequest = request;
                if (FailPushedSupport) throw new InvalidOperationException("The pushed-axis interval expired.");
                if (RefusePushedSupport)
                    return SupportFootprintResult.Refused(CapsuleFeatureStatus.CapacityExceeded,
                        SupportFootprintResult.MaximumElements + 1);
            }
            if (FootprintCalls == FailFootprintAt) throw new InvalidOperationException("The test interval expired.");
            if (RefuseFootprintFrom > 0 && FootprintCalls >= RefuseFootprintFrom)
                return SupportFootprintResult.Refused(CapsuleFeatureStatus.CapacityExceeded, SupportFootprintResult.MaximumElements + 1);
            SupportFootprintResult result = ((IPhysicsSupportFootprint)inner).QuerySupportFootprint(lease, request, members, joins, filter);
            if (result.Status == CapsuleFeatureStatus.Complete)
            {
                Assert.Same(lease, result.Lease);
                Assert.Same(inner, result.QueryWorld);
                Assert.Same(SourceWorld, result.SourceWorld);
            }
            return result;
        }

        public void AssertFootprintCurrent(in SupportFootprintResult result, IPhysicsQueryLease lease) =>
            ((IPhysicsSupportFootprint)inner).AssertFootprintCurrent(result, lease);

        public void AssertNeighborhoodCurrent(in SupportNeighborhoodResult result, IPhysicsQueryLease lease) =>
            ((IPhysicsSupportNeighborhood)inner).AssertNeighborhoodCurrent(result, lease);
        public bool Raycast(Vector3 origin, Vector3 direction, float distance, out RayHit hit, QueryFilter filter = default) =>
            inner.Raycast(origin, direction, distance, out hit, filter);
        public Pose GetDynamicPose(DynamicBodyHandle handle) => inner.GetDynamicPose(handle);
        public void GetDynamicVelocity(DynamicBodyHandle handle, out Vector3 linear, out Vector3 angular) =>
            inner.GetDynamicVelocity(handle, out linear, out angular);
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
}
