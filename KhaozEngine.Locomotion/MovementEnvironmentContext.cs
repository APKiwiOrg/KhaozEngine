using System;
using System.Threading;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

/// <summary>Composes a live physical read interval with one prepared, bounded environment pin.</summary>
public sealed class MovementEnvironmentContext
{
    readonly IPhysicsWorldQueryView _physics;
    readonly IPhysicsWorld _source;
    readonly IMovementEnvironmentProvider _provider;
    readonly MovementQueryIdentity _identity;
    readonly string _worldId;
    readonly ContactTickWorkspace _workspace = new();
    int _inUse;

    public MovementEnvironmentContext(IPhysicsWorldQueryView physics, IMovementEnvironmentProvider provider,
        MovementQueryIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(physics);
        ArgumentNullException.ThrowIfNull(provider);
        MovementEnvironmentValidation.Require(identity.IsValid, nameof(identity));
        _physics = physics;
        _source = physics.SourceWorld ?? throw new ArgumentException("A query view must name its source.", nameof(physics));
        _provider = provider;
        _identity = identity;
        _worldId = provider.WorldId;
        MovementEnvironmentValidation.RequireName(_worldId, nameof(provider));
    }

    /// <summary>Enter outside other physics leases. Success retains both gates until the returned lease is disposed.</summary>
    /// <remarks>Sequential per-tick acquisition is supported. Every fresh null-hint lease starts without
    /// reconstruction readiness. Rebuild selection on that lease, or pass a state with null Selection to the
    /// movement tick for automatic reconstruction. A carried selection alone cannot authorize a new lease.</remarks>
    public MovementAvailability TryAcquire(in MovementQueryScope scope, out MovementQueryLease? lease)
    {
        using var phase = ContactAllocationDiagnostics.Begin(ContactAllocationPhase.AcquisitionBody);
        lease = null;
        if (!scope.IsValid || scope.Identity != _identity ||
            !string.Equals(scope.WorldId, _worldId, StringComparison.Ordinal) ||
            !string.Equals(_provider.WorldId, _worldId, StringComparison.Ordinal) ||
            _physics is not IPhysicsQueryLeaseSource leaseSource || _physics is not IPhysicsCapsuleContacts)
            return MovementAvailability.Invalid;
        // Refuse nested or concurrent reuse before preparation could perform work inside an existing interval.
        if (Interlocked.CompareExchange(ref _inUse, 1, 0) != 0) return MovementAvailability.Invalid;
        IPhysicsQueryLease? physicsLease = null;
        IMovementEnvironmentPin? pin = null;
        bool transferred = false;
        try
        {
            MovementPreparationResult prepared;
            using (ContactAllocationDiagnostics.Begin(ContactAllocationPhase.Preparation)) prepared = _provider.Prepare(scope);
            if (!MovementEnvironmentValidation.Availability(prepared.Availability)) return MovementAvailability.Invalid;
            if (prepared.Availability != MovementAvailability.Known) return prepared.Availability;
            if (prepared.Identity != _identity || !string.Equals(_provider.WorldId, _worldId, StringComparison.Ordinal))
                return MovementAvailability.Invalid;

            using (ContactAllocationDiagnostics.Begin(ContactAllocationPhase.PhysicsLease)) physicsLease = leaseSource.AcquireQueryReadLease();
            if (physicsLease is null) return MovementAvailability.Invalid;
            physicsLease.AssertCurrent();
            if (!ReferenceEquals(physicsLease.SourceWorld, _source) ||
                !ReferenceEquals(_physics.SourceWorld, _source) || physicsLease.Origin != scope.Frame.PhysicsOrigin)
                return MovementAvailability.Invalid;

            SupportQueryDiagnostics.Record(SupportQueryCounter.ProducerPins);
            MovementAvailability availability;
            using (ContactAllocationDiagnostics.Begin(ContactAllocationPhase.ProducerPin)) availability = _provider.TryPinPrepared(scope, physicsLease, out pin);
            if (!MovementEnvironmentValidation.Availability(availability)) return MovementAvailability.Invalid;
            if (availability != MovementAvailability.Known) return availability;
            if (pin is null) return MovementAvailability.Invalid;
            try { pin.AssertCurrent(); }
            catch (InvalidOperationException) { return MovementAvailability.Stale; }

            // Capture once. All later query/result validation uses this same immutable witness.
            MovementScopeWitness witness = pin.Witness;
            long environmentGeneration = pin.EnvironmentGeneration;
            if (witness is null || !ReferenceEquals(pin.PhysicsView, _physics) ||
                !string.Equals(_provider.WorldId, _worldId, StringComparison.Ordinal) ||
                !ReferenceEquals(pin.PhysicsView.SourceWorld, _source) ||
                pin.GeometryGeneration != physicsLease.GeometryGeneration || environmentGeneration < 0 ||
                pin.Frame != scope.Frame || witness.Identity != _identity || witness.Scope.Identity != _identity ||
                witness.Scope.Frame != scope.Frame)
                return MovementAvailability.Invalid;
            if (!witness.CoversKnownEmptyRegions || !witness.Scope.Contains(scope)) return MovementAvailability.Unresolved;
            try { pin.AssertCurrent(); }
            catch (InvalidOperationException) { return MovementAvailability.Stale; }
            physicsLease.AssertCurrent();
            SupportQueryDiagnostics.Record(SupportQueryCounter.MovementLeases);
            using (ContactAllocationDiagnostics.Begin(ContactAllocationPhase.MovementLease))
                lease = new MovementQueryLease(_physics, physicsLease, pin, witness, scope.Frame, environmentGeneration, Release, _workspace);
            _workspace.Enter(lease);
            transferred = true;
            return MovementAvailability.Known;
        }
        finally
        {
            if (!transferred)
            {
                try { pin?.Dispose(); }
                finally
                {
                    try { physicsLease?.Dispose(); }
                    finally { Release(); }
                }
            }
        }
    }

    // Each context owns one workspace. Equal scopes or a shared view do not share that acquisition identity.
    internal bool OwnsLease(MovementQueryLease lease) =>
        lease.UsesPhysicsView(_physics) && ReferenceEquals(lease.Workspace, _workspace);

    void Release()
    {
        _workspace.EndInterval();
        Interlocked.Exchange(ref _inUse, 0);
    }
}
