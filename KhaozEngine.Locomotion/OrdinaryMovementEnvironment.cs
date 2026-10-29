using System;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

/// <summary>Certifies the resident ordinary world under its selected physical read interval.
/// Identity retains authored provenance. Fresh pins include the registration's live runtime revision.</summary>
public sealed partial class OrdinaryMovementEnvironment : IMovementEnvironmentProvider
{
    internal IPhysicsWorldQueryView Physics { get; }
    internal OrdinaryMovementRegistration Registration { get; }
    internal Func<float, float, float> GroundHeight { get; }
    internal Func<float, float, Vector3>? GroundNormal { get; }
    internal OrdinaryGroundSpace GroundSpace { get; }
    internal IMovementWaterField? WaterField { get; }
    internal MovementSpaceKey Space { get; }
    internal MovementSupportKey AnalyticOwner { get; }
    readonly MoveTuning _tuning;
    internal float CapsuleHalfHeight => _tuning.CapsuleHalfHeight;
    readonly MovementBoundary _boundary;
    readonly string[] _resources;
    readonly string? _waterSemanticIdentity;
    readonly MovementWaterExtent? _waterExtent;
    readonly object _cacheGate = new();
    MovementQueryScope _scope;
    MovementScopeWitness? _witness;
    MovementBoundary? _localBoundary;
    (long Physics, long Registration, long Water) _tuple;
    bool _hasTuple;
    long _generation;
    public string WorldId => Registration.WorldId;
    public MovementQueryIdentity Identity { get; }

    public OrdinaryMovementEnvironment(IPhysicsWorldQueryView physics, OrdinaryMovementRegistration registration,
        in MoveTuning tuning, Func<float, float, float> groundHeight, Func<float, float, Vector3>? groundNormal,
        MovementBoundary boundary, IMovementWaterField? waterField = null, OrdinaryGroundSpace groundSpace = OrdinaryGroundSpace.World)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(groundHeight);
        if (groundSpace is not (OrdinaryGroundSpace.World or OrdinaryGroundSpace.Frame))
            throw new ArgumentOutOfRangeException(nameof(groundSpace));
        OrdinaryMovementBoundary.Validate(boundary);
        MovementConfigurationValidation.ValidateOrdinaryContact(tuning, physics, true, true, false, waterField is not null);
        if (!ReferenceEquals(physics.SourceWorld, registration.SourceWorld))
            throw new ArgumentException("The ordinary view and registration must share their source world.", nameof(physics));
        Physics = physics;
        Registration = registration;
        _tuning = tuning;
        GroundHeight = groundHeight;
        GroundNormal = groundNormal;
        GroundSpace = groundSpace;
        _boundary = boundary;
        WaterField = waterField;
        _waterSemanticIdentity = waterField?.SemanticIdentity;
        _waterExtent = waterField?.Extent;
        if (waterField is not null)
            MovementEnvironmentValidation.Require(MovementEnvironmentValidation.Name(_waterSemanticIdentity) &&
                waterField.Generation >= 0, nameof(waterField));
        Space = new(WorldId, "ordinary");
        AnalyticOwner = new(WorldId, "analytic-ground");
        OrdinaryMovementExtent extent = registration.Extent;
        string text = string.Join("/", "ordinary-contact-v1", WorldId.Length, WorldId, registration.SemanticDigest,
            OrdinaryMovementRegistration.VectorBits(extent.Minimum), OrdinaryMovementRegistration.VectorBits(extent.Maximum),
            extent.AnalyticMinimumY is { } lower ? OrdinaryMovementRegistration.Bits(lower) : "absent",
            extent.AnalyticMaximumY is { } upper ? OrdinaryMovementRegistration.Bits(upper) : "absent",
            _waterSemanticIdentity?.Length ?? 0, _waterSemanticIdentity ?? "dry-absence",
            OrdinaryMovementRegistration.Bits(tuning.CapsuleRadius), OrdinaryMovementRegistration.Bits(tuning.CapsuleHalfHeight),
            OrdinaryMovementRegistration.Bits(tuning.StepHeight), OrdinaryMovementRegistration.Bits(tuning.MaxSlopeRadians),
            OrdinaryMovementRegistration.Bits(tuning.TractionHysteresisRadians), OrdinaryMovementRegistration.Bits(tuning.FootRadiusFraction),
            OrdinaryMovementRegistration.Bits(tuning.JumpSpeed), OrdinaryMovementRegistration.Bits(tuning.Gravity),
            (int)tuning.Water.Mode, OrdinaryMovementRegistration.Bits(tuning.Water.SurfaceJumpSpeed));
        if (groundSpace == OrdinaryGroundSpace.Frame) text += "/frame-analytic-ground";
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        Identity = new("ordinary-contact-v1", 1, digest);
        _resources = ["physics-selection/" + registration.SemanticDigest, "registration/" + registration.SemanticDigest,
            "analytic-field/" + digest, waterField is null ? "water/dry-absence" : "water/" + digest];
        // Validate the complete resident domain at configuration, before a head can start stepping.
        _ = CreateScope(new(default, physics.Origin, 0));
    }

    public MovementQueryScope CreateScope(in MovementFrameDescriptor frame)
    {
        lock (_cacheGate)
        {
            if (_witness is not null && _scope.Frame == frame) return _scope;
            MovementQueryScope scope = OrdinaryServedDomain.Create(Registration, _tuning, WaterField, Identity, frame);
            MovementAvailability result = MovementScopeWitness.TryCreate(scope, Identity, _resources, true, out var witness);
            if (result != MovementAvailability.Known) throw new ArgumentException("The ordinary domain cannot be witnessed.", nameof(frame));
            _scope = scope;
            _witness = witness;
            _localBoundary = OrdinaryMovementBoundary.Capture(_boundary, frame);
            return scope;
        }
    }

    internal MovementBoundary CaptureBoundary(in MovementFrameDescriptor frame)
    {
        lock (_cacheGate) { _ = CreateScope(frame); return _localBoundary!; }
    }

    public MovementPreparationResult Prepare(in MovementQueryScope scope)
    {
        if (!scope.IsValid || scope.WorldId != WorldId || scope.Identity != Identity || scope.CurrentSpace is not null)
            return new(MovementAvailability.Invalid, Identity);
        if (!WaterConfigurationCurrent()) return new(MovementAvailability.Unresolved, Identity);
        return new(scope == CreateScope(scope.Frame) ? MovementAvailability.Known : MovementAvailability.Unresolved, Identity);
    }

    public MovementAvailability TryPinPrepared(in MovementQueryScope scope, IPhysicsQueryLease physicsLease,
        out IMovementEnvironmentPin? pin)
    {
        pin = null;
        MovementAvailability prepared = Prepare(scope).Availability;
        if (prepared != MovementAvailability.Known) return prepared;
        physicsLease.AssertCurrent();
        if (!ReferenceEquals(physicsLease.SourceWorld, Physics.SourceWorld) || physicsLease.Origin != scope.Frame.PhysicsOrigin)
            return MovementAvailability.Invalid;
        MovementAvailability current = Registration.Authenticate(physicsLease);
        if (current != MovementAvailability.Known) return current;
        lock (_cacheGate)
        {
            _ = CreateScope(scope.Frame);
            var tuple = CaptureTuple(physicsLease);
            if (!_hasTuple || tuple != _tuple)
            {
                _generation = checked(_generation + 1);
                _tuple = tuple;
                _hasTuple = true;
            }
            pin = new OrdinaryMovementEnvironmentPin(this, physicsLease, scope, _witness!, _generation);
            if (tuple != CaptureTuple(physicsLease))
            {
                pin.Dispose();
                pin = null;
                return MovementAvailability.Unresolved;
            }
        }
        return MovementAvailability.Known;
    }

    internal (long Physics, long Registration, long Water) CaptureTuple(IPhysicsQueryLease lease) =>
        (lease.GeometryGeneration, Registration.Generation, WaterField?.Generation ?? 0);

    internal bool WaterConfigurationCurrent() => WaterField is null || WaterField.Generation >= 0 &&
        WaterField.SemanticIdentity == _waterSemanticIdentity && WaterField.Extent == _waterExtent;
}
