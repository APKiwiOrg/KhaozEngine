using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using static KhaozEngine.Tests.Movement.Fixtures.ContactNavigationFixture;

namespace KhaozEngine.Tests.Movement.Fixtures;

/// <summary>A borrowed movement view that forwards every query to a Bepu view and declares no concurrent reads.
/// <see cref="Fault"/> sees each capsule query's local start position and displacement and may throw. It must be a
/// pure function of those inputs so its failures do not depend on the schedule.</summary>
internal class ForwardingQueryView(IPhysicsWorldQueryView inner) : PhysicsForwarder(inner), IPhysicsWorldQueryView,
    IPhysicsQueryLeaseSource, IPhysicsCapsuleGeometryQueries, IPhysicsCapsuleFeatures, IPhysicsSupportNeighborhood,
    IPhysicsSupportFootprint
{
    internal Action<Vector3, Vector3>? Fault { get; init; }

    public IPhysicsWorld SourceWorld => inner.SourceWorld;
    public IPhysicsQueryLease AcquireQueryReadLease() => ((IPhysicsQueryLeaseSource)inner).AcquireQueryReadLease();

    public CapsuleContactResult QueryCapsuleContacts(CapsuleShape capsule, Pose pose, float separation,
        Span<CapsuleContact> destination, QueryFilter filter = default)
    {
        Fault?.Invoke(pose.Position, Vector3.Zero);
        return ((IPhysicsCapsuleContacts)inner).QueryCapsuleContacts(capsule, pose, separation, destination, filter);
    }

    public CapsuleContactResult QueryCapsuleContacts(in CapsuleQueryDimensions capsule, Pose pose, float separation,
        Span<CapsuleContact> destination, QueryFilter filter = default)
    {
        Fault?.Invoke(pose.Position, Vector3.Zero);
        return ((IPhysicsCapsuleGeometryQueries)inner).QueryCapsuleContacts(capsule, pose, separation, destination, filter);
    }

    public CapsuleSweepResult SweepCapsuleCertified(CapsuleShape capsule, Pose pose, Vector3 displacement,
        QueryFilter filter = default)
    {
        Fault?.Invoke(pose.Position, displacement);
        return ((IPhysicsCapsuleSweep)inner).SweepCapsuleCertified(capsule, pose, displacement, filter);
    }

    public CapsuleSweepResult SweepCapsuleCertified(in CapsuleQueryDimensions capsule, Pose pose, Vector3 displacement,
        QueryFilter filter = default)
    {
        Fault?.Invoke(pose.Position, displacement);
        return ((IPhysicsCapsuleGeometryQueries)inner).SweepCapsuleCertified(capsule, pose, displacement, filter);
    }

    public CapsuleFeatureResult QueryCapsuleFeature(IPhysicsQueryLease lease, StaticHandle target, CapsuleShape capsule,
        Pose pose, float maximumSeparationMetres, Span<CapsuleIncidentFace> faces, QueryFilter filter = default) =>
        ((IPhysicsCapsuleFeatures)inner).QueryCapsuleFeature(lease, target, capsule, pose, maximumSeparationMetres, faces, filter);

    public void AssertFeatureCurrent(in CapsuleFeatureResult result, IPhysicsQueryLease lease) =>
        ((IPhysicsCapsuleFeatures)inner).AssertFeatureCurrent(result, lease);

    public SupportNeighborhoodResult QuerySupportNeighborhood(IPhysicsQueryLease lease, CapsuleShape probe, Pose pose,
        float band, Span<SupportElement> elements, Span<ulong> joins, QueryFilter filter = default) =>
        ((IPhysicsSupportNeighborhood)inner).QuerySupportNeighborhood(lease, probe, pose, band, elements, joins, filter);

    public void AssertNeighborhoodCurrent(in SupportNeighborhoodResult result, IPhysicsQueryLease lease) =>
        ((IPhysicsSupportNeighborhood)inner).AssertNeighborhoodCurrent(result, lease);

    public SupportFootprintResult QuerySupportFootprint(IPhysicsQueryLease lease, in SupportFootprintRequest request,
        Span<SupportFootprintMember> members, Span<ulong> joins, QueryFilter filter = default) =>
        ((IPhysicsSupportFootprint)inner).QuerySupportFootprint(lease, request, members, joins, filter);

    public void AssertFootprintCurrent(in SupportFootprintResult result, IPhysicsQueryLease lease) =>
        ((IPhysicsSupportFootprint)inner).AssertFootprintCurrent(result, lease);
}

/// <summary>The same forwarding view after auditing its state: it holds only the inner view and an immutable fault, so
/// it declares concurrent reads.</summary>
internal sealed class ConcurrentForwardingQueryView(IPhysicsWorldQueryView inner) : ForwardingQueryView(inner),
    IPhysicsConcurrentReadSource;

/// <summary>A water field whose generation the test advances. Answers come from the inner field.</summary>
internal sealed class MutableWaterField(IMovementWaterField inner) : IMovementWaterField
{
    long _generation;

    public string SemanticIdentity => inner.SemanticIdentity;
    public long Generation => Interlocked.Read(ref _generation);
    public MovementWaterExtent Extent => inner.Extent;
    internal void Advance() => Interlocked.Increment(ref _generation);

    public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body, Vector3 physicsOrigin) =>
        inner.SampleCentreWater(body, physicsOrigin);

    public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 physicsOrigin,
        in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts) =>
        inner.TraceWater(query, physicsOrigin, identity, spans, contacts);
}

/// <summary>A fault raised by a test provider. The message names the fault and the exact query position.</summary>
internal sealed class ProviderFaultException(string tag, Vector3 position)
    : Exception($"{tag} at {BitConverter.SingleToInt32Bits(position.X)}, {BitConverter.SingleToInt32Bits(position.Y)}, " +
        $"{BitConverter.SingleToInt32Bits(position.Z)}")
{
    internal string Tag { get; } = tag;
}

/// <summary>A small two-column Stair world at zero origin: a left deck at 0.1875 and, on the right, a floor under a
/// slab at 0.375. Six rows of 0.3 m cells give raw surfaces, two layers and Stair candidates in every band.</summary>
internal sealed class SmallStairScene : IDisposable
{
    internal const float Cell = 0.3f;
    internal static MoveTuning StairTuning => ContactNavigationFixture.Tuning with
    { CapsuleRadius = 0.04f, CapsuleHalfHeight = 0.15625f, StepHeight = 0.21f, MaxStepClimbSpeed = 0 };
    internal static PhysicsNavBakeOptions StairOptions =>
        Options with { MinX = -0.3f, MaxX = 0.3f, MinZ = -0.9f, MaxZ = 0.9f, CellSize = Cell };

    readonly OrdinaryContactScene _scene;
    readonly IPhysicsWorldQueryView _inner;

    internal SmallStairScene(bool concurrentView = true, Action<Vector3, Vector3>? fault = null,
        IMovementWaterField? water = null)
    {
        _scene = new OrdinaryContactScene(StairTuning, install: (world, builder) =>
        {
            Box(world, builder, "floor", new(-8, -0.2f, -8), new(8, 0, 8));
            Box(world, builder, "left", new(-4, 0, -2), new(0, 0.1875f, 2));
            Box(world, builder, "right", new(0, 0.34375f, -2), new(4, 0.375f, 2));
        });
        _inner = _scene.Physics.CreateQueryViewExcludingStatics(_scene.Registration.ExcludedStatics.ToArray());
        View = concurrentView ? new ConcurrentForwardingQueryView(_inner) { Fault = fault }
            : new ForwardingQueryView(_inner) { Fault = fault };
        Context = new GroundMoveContext(_scene.Height, _scene.Normal, _scene.Physics, null, null, View, water,
            _scene.Registration, MovementBoundary.Rectangle(-20, -20, 20, 20));
    }

    internal BepuPhysicsWorld Physics => _scene.Physics;
    internal ForwardingQueryView View { get; }
    internal GroundMoveContext Context { get; }

    internal PhysicsNavBake Capture() => PhysicsNavBake.Capture(Context, StairOptions, _ => 0);

    internal GroundNavigation Build(NavBakeExecution? execution)
    {
        using PhysicsNavBake capture = Capture();
        return capture.BuildProfile(StairTuning, default, GroundProfileOptions.Default, execution);
    }

    /// <summary>The world and the borrowed view still serve reads, and an exclusive writer can still enter the world
    /// from another thread. Disposing a fresh view is a writer that leaves the geometry generation, and so the
    /// registration, unchanged. A leaked read lease would hold the writer back past the bounded wait.</summary>
    internal void AssertUsable()
    {
        Xunit.Assert.True(View.Raycast(new(0.15f, 2, 0.15f), -Vector3.UnitY, 4, out _));
        Xunit.Assert.True(Physics.Raycast(new(-0.15f, 2, 0.15f), -Vector3.UnitY, 4, out _));
        IPhysicsWorldQueryView probe = Physics.CreateQueryViewExcludingStatics([]);
        Xunit.Assert.True(Task.Run(probe.Dispose).Wait(TimeSpan.FromSeconds(30)), "A writer could not enter the world.");
    }

    public void Dispose()
    {
        Context.Dispose();
        _inner.Dispose();
        _scene.Dispose();
    }
}
