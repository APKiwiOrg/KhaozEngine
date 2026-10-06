using System;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

/// <summary>Preparation is ungated. Pinning uses already-prepared resources under the physical read gate.</summary>
public interface IMovementEnvironmentProvider
{
    /// <summary>Actual served world identity, bound by the producer factory rather than the request.</summary>
    string WorldId { get; }
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
    MovementWaterPoint SampleCentreWater(in MovementBodyQuery body);
    MovementSupportSet EnumerateSupport(in MovementSupportRequest request, Span<MovementSupportCandidate> candidates);
    MovementCoverageResult TraceWater(in MovementMediumSweepQuery query,
        Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts);
    MovementAvailability RebuildSelection(in FramedMovementState state, out MovementSelection selection);
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
