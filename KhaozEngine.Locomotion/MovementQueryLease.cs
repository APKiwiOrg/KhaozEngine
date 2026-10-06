using System;
using System.Runtime.CompilerServices;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

/// <summary>Process-local lifetime identity. Source comparison and hashing use object identity only.</summary>
public readonly struct MovementQueryLeaseId : IEquatable<MovementQueryLeaseId>
{
    public IPhysicsWorld SourceWorld { get; }
    public long GeometryGeneration { get; }
    public long EnvironmentGeneration { get; }
    public ulong FrameEpoch { get; }

    internal MovementQueryLeaseId(IPhysicsWorld sourceWorld, long geometryGeneration, long environmentGeneration,
        ulong frameEpoch)
    {
        SourceWorld = sourceWorld;
        GeometryGeneration = geometryGeneration;
        EnvironmentGeneration = environmentGeneration;
        FrameEpoch = frameEpoch;
    }

    public bool Equals(MovementQueryLeaseId other) => ReferenceEquals(SourceWorld, other.SourceWorld) &&
        GeometryGeneration == other.GeometryGeneration && EnvironmentGeneration == other.EnvironmentGeneration &&
        FrameEpoch == other.FrameEpoch;
    public override bool Equals(object? obj) => obj is MovementQueryLeaseId other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(SourceWorld is null ? 0 : RuntimeHelpers.GetHashCode(SourceWorld),
        GeometryGeneration, EnvironmentGeneration, FrameEpoch);
    public static bool operator ==(MovementQueryLeaseId left, MovementQueryLeaseId right) => left.Equals(right);
    public static bool operator !=(MovementQueryLeaseId left, MovementQueryLeaseId right) => !left.Equals(right);
}

/// <summary>Thread-affine read interval. Publish pure outputs before disposal and mutate physics afterward.</summary>
public sealed partial class MovementQueryLease : IDisposable
{
    readonly IPhysicsWorldQueryView _view;
    readonly IPhysicsQueryLease _physics;
    readonly IMovementEnvironmentPin _pin;
    readonly Action _releaseContext;
    readonly int _thread = Environment.CurrentManagedThreadId;
    bool _disposed;

    public MovementQueryLeaseId Id { get; }
    public MovementScopeWitness Witness { get; }
    public MovementQueryIdentity Identity => Witness.Identity;
    public MovementFrameDescriptor Frame { get; }

    internal MovementQueryLease(IPhysicsWorldQueryView view, IPhysicsQueryLease physics,
        IMovementEnvironmentPin pin, MovementScopeWitness witness, MovementFrameDescriptor frame,
        long environmentGeneration, Action releaseContext)
    {
        _view = view;
        _physics = physics;
        _pin = pin;
        Witness = witness;
        Frame = frame;
        _releaseContext = releaseContext;
        Id = new MovementQueryLeaseId(physics.SourceWorld, physics.GeometryGeneration, environmentGeneration, frame.Epoch);
    }

    public void AssertCurrent()
    {
        AssertThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _physics.AssertCurrent();
        _pin.AssertCurrent();
        if (!ReferenceEquals(_physics.SourceWorld, Id.SourceWorld) ||
            !ReferenceEquals(_view.SourceWorld, Id.SourceWorld) || !ReferenceEquals(_pin.PhysicsView, _view) ||
            _physics.GeometryGeneration != Id.GeometryGeneration || _pin.GeometryGeneration != Id.GeometryGeneration ||
            _pin.EnvironmentGeneration != Id.EnvironmentGeneration || _pin.Frame != Frame || _physics.Origin != Frame.PhysicsOrigin)
            throw new InvalidOperationException("The movement query binding is stale.");
    }

    public void Dispose()
    {
        AssertThread();
        if (_disposed) return;
        _disposed = true;
        try { _pin.Dispose(); }
        finally
        {
            try { _physics.Dispose(); }
            finally { _releaseContext(); }
        }
    }

    void AssertThread()
    {
        if (Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("A movement query lease belongs to its acquiring thread.");
    }
}
