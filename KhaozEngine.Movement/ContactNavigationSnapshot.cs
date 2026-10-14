using System;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;

namespace KhaozEngine.Movement;

/// <summary>The one basis a parallel contact build works over: physics geometry and origin, the navigation frame, the
/// selected query view and the environment producers every worker binding reads. The registration and boundary are
/// fixed by the context, so their identity cannot drift. Every worker authenticates its first lease against the
/// snapshot, and the build checks it again after every worker joins and before anything is published.</summary>
internal sealed class ContactNavigationSnapshot
{
    readonly IPhysicsWorld _physics;
    readonly IPhysicsWorldQueryView _view;
    readonly OrdinaryMovementRegistration _registration;
    readonly IMovementWaterField? _water;
    readonly long _waterGeneration;
    readonly string? _waterIdentity;
    readonly MovementWaterExtent? _waterExtent;

    ContactNavigationSnapshot(PhysicsNavCaptureBasis basis, MovementFrameDescriptor frame, GroundMoveContext context)
    {
        Basis = basis;
        Frame = frame;
        _physics = context.Physics!;
        _view = context.MovementQueries!;
        _registration = context.MovementRegistration!;
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
            lease.Frame != Frame || !ProducersCurrent())
            throw Changed();
    }

    /// <summary>Refuses the build when the physics basis, the view or a producer differs from the snapshot.</summary>
    internal void AssertCurrent()
    {
        Basis.AssertCurrent();
        if (!ReferenceEquals(_view.SourceWorld, _physics) || !ReferenceEquals(_registration.SourceWorld, _physics) ||
            _view.Origin != Frame.PhysicsOrigin || Frame.PhysicsOrigin != Basis.Origin || !ProducersCurrent())
            throw Changed();
    }

    bool ProducersCurrent() => _water is null || _water.Generation == _waterGeneration &&
        string.Equals(_water.SemanticIdentity, _waterIdentity, StringComparison.Ordinal) && _water.Extent == _waterExtent;

    static InvalidOperationException Changed() =>
        new("The contact navigation build snapshot changed. The whole build is refused.");
}
