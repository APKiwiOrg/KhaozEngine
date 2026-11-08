using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;

namespace KhaozEngine.Movement;

/// <summary>The one basis a stable contact build works over: physics geometry and origin, the navigation frame, the
/// selected query view and the environment producers every worker binding reads. The context fixes the boundary and
/// authored registration identity. The live registration revision is captured separately. Every worker authenticates
/// its first lease against the snapshot, and the build checks it again after every worker joins and before anything
/// is published.</summary>
internal sealed class ContactNavigationSnapshot
{
    readonly IPhysicsWorld _physics;
    readonly IPhysicsWorldQueryView _view;
    readonly OrdinaryMovementRegistration _registration;
    readonly long _registrationGeneration;
    readonly IMovementWaterField? _water;
    readonly long _waterGeneration;
    readonly string? _waterIdentity;
    readonly MovementWaterExtent? _waterExtent;
    readonly GroundMoveContext _context;

    ContactNavigationSnapshot(PhysicsNavCaptureBasis basis, MovementFrameDescriptor frame, GroundMoveContext context)
    {
        Basis = basis;
        _context = context;
        Frame = frame;
        _physics = context.Physics!;
        _view = context.MovementQueries!;
        _registration = context.MovementRegistration!;
        _registrationGeneration = _registration.Generation;
        _water = context.MovementWaterField;
        _waterGeneration = _water?.Generation ?? 0;
        _waterIdentity = _water?.SemanticIdentity;
        _waterExtent = _water?.Extent;
    }

    internal PhysicsNavCaptureBasis Basis { get; }
    internal MovementFrameDescriptor Frame { get; }

    /// <summary>Captures the snapshot on the calling thread before any worker exists.</summary>
    internal static ContactNavigationSnapshot Capture(PhysicsNavBake bake, GroundMoveContext context)
    {
        PhysicsNavCaptureBasis basis = bake.Basis
            ?? throw new NavigationBuildException(NavigationBuildRefusal.CaptureLeaseRequired,
                    "Contact navigation requires a capture from an IPhysicsQueryLeaseSource.")
                    { RequiredCapability = typeof(KhaozEngine.Physics.IPhysicsQueryLeaseSource) };
        basis.AssertCurrent();
        var snapshot = new ContactNavigationSnapshot(basis, context.NavigationFrame, context);
        snapshot.AssertCurrent();
        return snapshot;
    }

    const string ChangedMessage = "The contact navigation build snapshot changed. The whole build is refused.";

    /// <summary>Authenticates a worker's first lease, held by the caller, against the snapshot.</summary>
    internal void Authenticate(MovementQueryLease lease)
    {
        AssertLease(lease.Id, lease.Frame);
        AssertProducersCurrent();
    }

    void AssertLease(MovementQueryLeaseId id, MovementFrameDescriptor frame)
    {
        if (!ReferenceEquals(id.SourceWorld, Basis.Source))
            throw new NavigationBuildException(NavigationBuildRefusal.SnapshotLeaseSourceChanged, ChangedMessage)
                { SourceRole = NavigationSourceRole.ReadLease };
        if (id.GeometryGeneration != Basis.GeometryGeneration)
            throw new NavigationBuildException(NavigationBuildRefusal.SnapshotLeaseGenerationChanged, ChangedMessage)
                { ExpectedGeneration = Basis.GeometryGeneration, ActualGeneration = id.GeometryGeneration };
        if (!Same(frame, Frame))
            throw new NavigationBuildException(NavigationBuildRefusal.SnapshotLeaseFrameChanged, ChangedMessage)
                { ExpectedFrame = Frame, ActualFrame = frame };
    }

    /// <summary>Refuses the build when the physics basis, the view or a producer differs from the snapshot.</summary>
    internal void AssertCurrent()
    {
        Basis.AssertCurrent();
        if (!ReferenceEquals(_view.SourceWorld, _physics))
            throw new NavigationBuildException(NavigationBuildRefusal.SnapshotViewSourceChanged, ChangedMessage)
                { SourceRole = NavigationSourceRole.MovementQueryView };
        // GroundMoveContext rejects a registration from another source at construction. Both references and
        // OrdinaryMovementRegistration.SourceWorld are immutable, so no input can reach this invariant.
        if (!ReferenceEquals(_registration.SourceWorld, _physics)) throw new InvalidOperationException(ChangedMessage);
        Vector3 origin = _view.Origin;
        if (!Same(origin, Frame.PhysicsOrigin))
            throw new NavigationBuildException(NavigationBuildRefusal.SnapshotViewOriginChanged, ChangedMessage)
                { ExpectedOrigin = Frame.PhysicsOrigin, ActualOrigin = origin };
        AssertBasisOrigin(Frame);
        AssertContextFrame(_context.NavigationFrame);
        AssertProducersCurrent();
    }

    void AssertBasisOrigin(MovementFrameDescriptor frame)
    {
        if (!Same(frame.PhysicsOrigin, Basis.Origin))
            throw new NavigationBuildException(NavigationBuildRefusal.SnapshotBasisOriginChanged, ChangedMessage)
                { ExpectedOrigin = Basis.Origin, ActualOrigin = frame.PhysicsOrigin };
    }

    void AssertContextFrame(MovementFrameDescriptor actual)
    {
        if (!Same(actual, Frame))
            throw new NavigationBuildException(NavigationBuildRefusal.SnapshotContextFrameChanged, ChangedMessage)
                { ExpectedFrame = Frame, ActualFrame = actual };
    }

    void AssertRegistrationGeneration(long actual)
    {
        if (actual != _registrationGeneration)
            throw new NavigationBuildException(NavigationBuildRefusal.RegistrationGenerationChanged, ChangedMessage)
                { ExpectedGeneration = _registrationGeneration, ActualGeneration = actual };
    }

    static void AssertNoRuntimeStatics(bool present)
    {
        if (present) throw new NavigationBuildException(NavigationBuildRefusal.RuntimeStaticsPresent, ChangedMessage)
            { SourceRole = NavigationSourceRole.MovementRegistration };
    }

    void AssertProducersCurrent()
    {
        AssertRegistrationGeneration(_registration.Generation);
        AssertNoRuntimeStatics(_registration.HasRuntimeStatics);
        if (_water is null) return;
        long generation = _water.Generation;
        if (generation != _waterGeneration)
            throw new NavigationBuildException(NavigationBuildRefusal.WaterGenerationChanged, ChangedMessage)
                { ExpectedGeneration = _waterGeneration, ActualGeneration = generation };
        string identity = _water.SemanticIdentity;
        if (!string.Equals(identity, _waterIdentity, StringComparison.Ordinal))
            throw new NavigationBuildException(NavigationBuildRefusal.WaterIdentityChanged, ChangedMessage)
                { ExpectedIdentity = _waterIdentity, ActualIdentity = identity };
        MovementWaterExtent extent = _water.Extent;
        if (!Same(extent, _waterExtent))
            throw new NavigationBuildException(NavigationBuildRefusal.WaterExtentChanged, ChangedMessage)
                { ExpectedExtent = _waterExtent, ActualExtent = extent };
    }

    // Fault values enter only the real identity guards, never the retained snapshot or a live producer.
    internal void InjectChangedLeaseForTests(MovementQueryLeaseId id, MovementFrameDescriptor frame) => AssertLease(id, frame);
    internal void InjectChangedFrameForTests(MovementFrameDescriptor frame, bool contextFrame)
    {
        if (contextFrame) AssertContextFrame(frame);
        else AssertBasisOrigin(frame);
    }
    internal void InjectChangedRegistrationForTests(long generation, bool runtimeStatics)
    {
        AssertRegistrationGeneration(generation);
        AssertNoRuntimeStatics(runtimeStatics);
    }

    static bool Same(MovementWaterExtent a, MovementWaterExtent? b) => b is { } extent &&
        Same(a.Minimum, extent.Minimum) && Same(a.Maximum, extent.Maximum);
    static bool Same(MovementFrameDescriptor a, MovementFrameDescriptor b) =>
        a.Frame == b.Frame && a.Epoch == b.Epoch && Same(a.PhysicsOrigin, b.PhysicsOrigin);
    static bool Same(Vector3 a, Vector3 b) => Same(a.X, b.X) && Same(a.Y, b.Y) && Same(a.Z, b.Z);
    static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
}
