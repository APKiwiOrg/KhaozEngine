using System;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

/// <summary>Preparation is ungated. Pinning uses already-prepared resources under the physical read gate.</summary>
public interface IMovementEnvironmentProvider
{
    MovementPreparationResult Prepare(in MovementQueryScope scope);
    MovementAvailability TryPinPrepared(in MovementQueryScope scope, IPhysicsQueryLease physicsLease,
        out IMovementEnvironmentPin? pin);
}

/// <summary>Local binding and lifetime for immutable canonical environment evidence.</summary>
public interface IMovementEnvironmentPin : IDisposable
{
    IPhysicsWorldQueryView PhysicsView { get; }
    long GeometryGeneration { get; }
    long EnvironmentGeneration { get; }
    MovementFrameDescriptor Frame { get; }
    MovementScopeWitness Witness { get; }
    void AssertCurrent();
}

/// <summary>Portable identity of a completed preparation, or an explicit refusal.</summary>
public readonly record struct MovementPreparationResult
{
    public MovementAvailability Availability { get; }
    public MovementQueryIdentity Identity { get; }

    public MovementPreparationResult(MovementAvailability availability, MovementQueryIdentity identity)
    {
        MovementEnvironmentValidation.Require(MovementEnvironmentValidation.Availability(availability), nameof(availability));
        MovementEnvironmentValidation.Require(availability != MovementAvailability.Known || identity.IsValid, nameof(identity));
        Availability = availability;
        Identity = identity;
    }
}
