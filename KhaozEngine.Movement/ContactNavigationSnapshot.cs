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
            ?? throw new InvalidOperationException("Contact navigation requires a capture from an IPhysicsQueryLeaseSource.");
        basis.AssertCurrent();
        var snapshot = new ContactNavigationSnapshot(basis, context.NavigationFrame, context);
        snapshot.AssertCurrent();
        return snapshot;
    }

    /// <summary>Authenticates a worker's first lease, held by the caller, against the snapshot.</summary>
    internal void Authenticate(MovementQueryLease lease)
    {
        if (!ReferenceEquals(lease.Id.SourceWorld, Basis.Source) || lease.Id.GeometryGeneration != Basis.GeometryGeneration ||
            !Same(lease.Frame, Frame) || !ProducersCurrent())
            throw Changed();
    }

    /// <summary>Refuses the build when the physics basis, the view or a producer differs from the snapshot.</summary>
    internal void AssertCurrent()
    {
        Basis.AssertCurrent();
        if (!ReferenceEquals(_view.SourceWorld, _physics) || !ReferenceEquals(_registration.SourceWorld, _physics) ||
            !Same(_view.Origin, Frame.PhysicsOrigin) || !Same(Frame.PhysicsOrigin, Basis.Origin) ||
            !Same(_context.NavigationFrame, Frame) || !ProducersCurrent())
            throw Changed();
    }

    bool ProducersCurrent() => _registration.Generation == _registrationGeneration && !_registration.HasRuntimeStatics &&
        (_water is null || _water.Generation == _waterGeneration &&
        string.Equals(_water.SemanticIdentity, _waterIdentity, StringComparison.Ordinal) && Same(_water.Extent, _waterExtent));

    static bool Same(MovementWaterExtent a, MovementWaterExtent? b) => b is { } extent &&
        Same(a.Minimum, extent.Minimum) && Same(a.Maximum, extent.Maximum);
    static bool Same(MovementFrameDescriptor a, MovementFrameDescriptor b) =>
        a.Frame == b.Frame && a.Epoch == b.Epoch && Same(a.PhysicsOrigin, b.PhysicsOrigin);
    static bool Same(Vector3 a, Vector3 b) => Same(a.X, b.X) && Same(a.Y, b.Y) && Same(a.Z, b.Z);
    static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

    static InvalidOperationException Changed() =>
        new("The contact navigation build snapshot changed. The whole build is refused.");
}
