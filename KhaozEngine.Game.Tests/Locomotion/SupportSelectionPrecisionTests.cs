using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public class SupportSelectionPrecisionTests
{
    [Theory]
    [InlineData(-0.00075f, false)]
    [InlineData(0f, true)]
    [InlineData(-0.00004687509499490261f, true)]
    [InlineData(-0.00004687509863288142f, false)]
    public void PublicSelectionChargesTranslationAndExplicitPlacementToOnePrecisionBudget(float bed, bool admitted)
    {
        using var physics = new BepuPhysicsWorld();
        physics.Rebase(new(0, -16384, 0));
        using var raw = physics.CreateQueryViewExcludingStatics([]);
        using var view = new EmptyView(raw);
        var builder = OrdinaryMovementRegistration.CreateBuilder("world", physics,
            new(new(-20, -4, -20), new(20, 4, 20), bed, bed));
        OrdinaryMovementRegistration registration;
        using (var read = physics.AcquireQueryReadLease()) registration = builder.Build(read);
        var tuning = MoveTuning.Default with
        {
            Controller = MovementControllerKind.Contact, CapsuleRadius = 0.25f, CapsuleHalfHeight = 0.75f,
            Water = new(WaterTraversalMode.DryOnly),
        };
        var ordinary = new OrdinaryMovementEnvironment(view, registration, tuning, (_, _) => bed, null,
            MovementBoundary.Rectangle(-20, -20, 20, 20));
        var frame = new MovementFrameDescriptor(WorldFrame.Origin, physics.Origin, 1);
        var ordinaryScope = ordinary.CreateScope(frame);
        var space = new MovementSpaceKey("world", "ordinary");
        var owner = new MovementSupportKey("world", "analytic-ground");
        var scope = new MovementQueryScope(new(-20, 16370, -20), new(20, 16400, 20), 2, 2,
            space, ordinary.Identity, frame);
        var environment = new EnvironmentAcquisitionFixture(view, scope) { ContactSupportEnabled = true };
        IMovementContactSupportPin producer = null!;
        MovementAnalyticGround? captured = null;
        environment.OnAnalyticGround = (in MovementSupportRequest request, out MovementAnalyticGround? ground) =>
        {
            MovementAvailability result = producer.SampleAnalyticGround(request, out ground);
            captured = ground;
            return result;
        };
        environment.OnContactSupport = (in MovementContactSupportRequest request, out MovementContactSupportBinding binding) =>
            producer.BindContactSupport(request, out binding);
        environment.OnSupport = (in MovementSupportRequest _, Span<MovementSupportCandidate> candidates) =>
        {
            candidates[0] = new(owner, space, new(0, 16384, 0), Vector3.UnitY, null);
            return new(MovementAvailability.Known, 1, 1, ordinary.Identity);
        };
        var acquired = environment.Acquire();
        Assert.Equal(MovementAvailability.Known, acquired.Status);
        using var lease = Assert.IsType<MovementQueryLease>(acquired.Lease);
        Assert.Equal(MovementAvailability.Known,
            ordinary.TryPinPrepared(ordinaryScope, environment.LastPhysicsLease!, out var pin));
        using var owned = Assert.IsAssignableFrom<IMovementEnvironmentPin>(pin);
        producer = Assert.IsAssignableFrom<IMovementContactSupportPin>(owned);
        var body = new MovementBodyQuery(new(0, 16384.75f, 0), 0.25f, 0.75f, space, null);
        var request = new MovementSupportRequest(body, 0.5f, 0.5f, tuning.MaxSlopeRadians, new(space, body.Feet));

        MovementAvailability selected = MovementSupportResolver.Select(request, tuning, lease, out var placement);

        Assert.NotNull(captured);
        Assert.Equal(16384f, captured.Value.Height);
        if (placement is { } actual)
        {
            // These fixture sums are exact in binary64. The third bed is the exact budget boundary,
            // and the fourth is its adjacent float on the refused side.
            double feet = (double)actual.Centre.Y - 0.75f + physics.Origin.Y;
            double error = feet - bed - 0.001f;
            Assert.True(feet >= bed);
            Assert.True(error <= MovementQueryLease.CoverageSkinMetres,
                $"Selected numerical displacement {error:R} exceeds {MovementQueryLease.CoverageSkinMetres:R}.");
        }
        Assert.Equal(admitted ? MovementAvailability.Known : MovementAvailability.Unresolved, selected);
        if (admitted)
        {
            Assert.NotNull(placement);
            Assert.Equal(owner, placement.Value.Candidate.Owner);
            Assert.Equal(16384.751953125f, placement.Value.Centre.Y);
        }
        else Assert.Null(placement);
    }

    // There are no installed bodies. Empty certificates are therefore exact everywhere, independently
    // of the underlying backend's coordinate domain. The real ordinary producer supplies all support.
    sealed class EmptyView(IPhysicsWorldQueryView inner) : IPhysicsWorldQueryView, IPhysicsQueryLeaseSource,
        IPhysicsCapsuleContacts, IPhysicsCapsuleSweep, IPhysicsSupportFootprint
    {
        public IPhysicsWorld SourceWorld => inner.SourceWorld;
        public Vector3 Origin => inner.Origin;
        public IPhysicsQueryLease AcquireQueryReadLease() => ((IPhysicsQueryLeaseSource)inner).AcquireQueryReadLease();
        public CapsuleContactResult QueryCapsuleContacts(CapsuleShape capsule, Pose pose, float margin,
            Span<CapsuleContact> destination, QueryFilter filter = default) => new(true, 0, 0, 0);
        public CapsuleSweepResult SweepCapsuleCertified(CapsuleShape capsule, Pose pose, Vector3 delta,
            QueryFilter filter = default) => new(CapsuleSweepStatus.Clear, float.MaxValue, null, 0);
        public SupportFootprintResult QuerySupportFootprint(IPhysicsQueryLease lease, in SupportFootprintRequest request,
            Span<SupportFootprintMember> members, Span<ulong> joins, QueryFilter filter = default)
        {
            lease.AssertCurrent();
            return SupportFootprintResult.Completed(this, lease, 0, 0, default, default);
        }
        public void AssertFootprintCurrent(in SupportFootprintResult result, IPhysicsQueryLease lease)
        {
            lease.AssertCurrent();
            if (!ReferenceEquals(result.QueryWorld, this) || !ReferenceEquals(result.SourceWorld, SourceWorld) ||
                !ReferenceEquals(result.Lease, lease) || result.Origin != Origin || result.GeometryGeneration != lease.GeometryGeneration)
                throw new InvalidOperationException("Foreign empty certificate.");
        }
        public bool Raycast(Vector3 origin, Vector3 direction, float distance, out RayHit hit, QueryFilter filter = default)
        { hit = default; return false; }
        public bool SweepCapsule(CapsuleShape shape, Pose pose, Vector3 direction, float distance, out SweepHit hit, QueryFilter filter = default)
        { hit = default; return false; }
        public bool ComputePenetration(CapsuleShape shape, Pose pose, out Vector3 mtv) { mtv = default; return false; }
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
        public void Dispose() { }
    }
}
