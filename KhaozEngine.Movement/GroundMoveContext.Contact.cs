using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;

namespace KhaozEngine.Movement;

public sealed partial class GroundMoveContext
{
    readonly bool _contactConstruction;
    readonly string? _legacyOnlyContactRefusal;
    readonly GroundContactProducerKind? _contactProducerKind;
    readonly GroundContactCapability? _requiredContactCapability;
    readonly Func<float, float, float, MovementMedium>? _providedMedium;
    bool _ownsMovementQueries;
    bool _disposed;
    EffectiveMovementBinding? _binding;
    MoveTuning _validatedTuning;
    bool _hasValidatedTuning;
    MovementFrameDescriptor _contactFrame;
    bool _frameCaptured;

    /// <summary>Constructs an absolute-coordinate context with an ordinary contact environment. Views supplied
    /// by the caller stay borrowed. Otherwise the context creates and owns one selection when a registration and
    /// physics world are supplied. Contact tuning is validated on first use and cached by value.
    /// <paramref name="legacyOnlyContactRefusal"/> is an optional construction-time restriction for producers whose
    /// geometry supports only legacy movement. Every contact use throws GroundContactCapabilityException with
    /// ContactProducerRestricted and this text as Restriction. Null permits contact configuration.</summary>
    public GroundMoveContext(Func<float, float, float> groundHeight,
        Func<float, float, Vector3>? groundNormal, IPhysicsWorld? physics,
        Func<float, float, Vector2>? clampXz, Func<float, float, float, MovementMedium>? medium,
        IPhysicsWorldQueryView? movementQueries, IMovementWaterField? movementWaterField,
        OrdinaryMovementRegistration? movementRegistration, MovementBoundary? movementBoundary,
        string? legacyOnlyContactRefusal = null)
        : this(groundHeight, groundNormal, physics, clampXz, medium, movementQueries, movementWaterField,
            movementRegistration, movementBoundary, legacyOnlyContactRefusal, contactConstruction: true)
    { }

    /// <summary>Constructs an ordinary context with optional producer restriction metadata. The restriction's text
    /// and admission are the same as the string-only overload. Known engine producers additionally name their kind
    /// and required capability, which contact refusals carry without interpreting the restriction text.</summary>
    public GroundMoveContext(Func<float, float, float> groundHeight,
        Func<float, float, Vector3>? groundNormal, IPhysicsWorld? physics,
        Func<float, float, Vector2>? clampXz, Func<float, float, float, MovementMedium>? medium,
        IPhysicsWorldQueryView? movementQueries, IMovementWaterField? movementWaterField,
        OrdinaryMovementRegistration? movementRegistration, MovementBoundary? movementBoundary,
        string? legacyOnlyContactRefusal, GroundContactProducerKind? producerKind,
        GroundContactCapability? requiredCapability)
        : this(groundHeight, groundNormal, physics, clampXz, medium, movementQueries, movementWaterField,
            movementRegistration, movementBoundary, legacyOnlyContactRefusal, contactConstruction: true,
            producerKind, requiredCapability)
    { }

    public IMovementWaterField? MovementWaterField { get; }
    public OrdinaryMovementRegistration? MovementRegistration { get; }
    public MovementBoundary? MovementBoundary { get; }
    /// <summary>Whether the construction captured every capability required by ordinary contact movement.
    /// Tuning validation and live field, registration and frame authentication still run before movement.</summary>
    public bool SupportsContactController { get; }

    internal Func<float, float, float, MovementMedium>? CaptureMedium => MovementWaterField is null
        ? Medium
        : _providedMedium ?? throw new InvalidOperationException(
            "Water sampling with a contact water field requires a medium delegate.");

    IPhysicsWorldQueryView? CreateMovementView(IPhysicsWorldQueryView? supplied)
    {
        if (MovementRegistration is { } registration && !ReferenceEquals(registration.SourceWorld, Physics))
            throw new ArgumentException("The registration must name the configured physical source.", nameof(MovementRegistration));
        if (supplied is not null || !_contactConstruction || _legacyOnlyContactRefusal is not null ||
            MovementRegistration is null || Physics is null) return supplied;
        var view = Physics.CreateQueryViewExcludingStatics(MovementRegistration.ExcludedStatics.ToArray());
        if (!ReferenceEquals(view.SourceWorld, Physics))
        {
            view.Dispose();
            throw new ArgumentException("Movement queries must belong to the complete physics world.");
        }
        _ownsMovementQueries = true;
        return view;
    }

    internal void ValidateTuning(in MoveTuning tuning)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stepping) throw new InvalidOperationException("Ground movement steps cannot overlap or recurse.");
        if (tuning.Controller == MovementControllerKind.Contact && _legacyOnlyContactRefusal is not null)
            throw ContactProducerRefusal();
        if (_hasValidatedTuning && _validatedTuning == tuning) return;
        EffectiveMovementBinding next;
        if (tuning.Controller == MovementControllerKind.Contact)
        {
            next = CreateNavigationBinding(tuning);
        }
        else
        {
            CheckTuning(tuning);
            if (_binding is { Kind: EffectiveMovementBindingKind.Legacy } &&
                _binding.Tuning with { WalkSpeed = tuning.WalkSpeed, RunSpeed = tuning.RunSpeed, AirMomentum = tuning.AirMomentum } == tuning)
            {
                // Probe pace overrides change neither legacy binding selection nor navigation compatibility.
                MovementConfigurationValidation.ValidateLegacy(tuning);
                _validatedTuning = tuning;
                _hasValidatedTuning = true;
                return;
            }
            next = EffectiveMovementBinding.Legacy(tuning);
        }
        _binding?.Dispose();
        _binding = next;
        _validatedTuning = tuning;
        _hasValidatedTuning = true;
    }

    internal EffectiveMovementBinding ResolvedBinding => _binding
        ?? throw new InvalidOperationException("Validate tuning before reading the resolved movement binding.");

    internal EffectiveMovementBinding CreateNavigationBinding(in MoveTuning tuning)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stepping) throw new InvalidOperationException("Ground movement steps cannot overlap or recurse.");
        if (_legacyOnlyContactRefusal is not null) throw ContactProducerRefusal();
        if (!_contactConstruction)
            throw new GroundContactCapabilityException(GroundContactCapabilityRefusal.OrdinaryContextRequired,
                "Contact movement requires a GroundMoveContext constructed with its ordinary environment.")
            { RequiredKind = EffectiveMovementBindingKind.OrdinaryContact };
        EffectiveMovementBinding.Preflight(tuning, EffectiveMovementBindingKind.OrdinaryContact);
        if (Physics is null || MovementRegistration is null || MovementBoundary is null)
            throw new MovementConfigurationException(Physics is null ? MovementConfigurationRefusal.PhysicsRequired
                : MovementRegistration is null ? MovementConfigurationRefusal.RegistrationRequired : MovementConfigurationRefusal.BoundaryRequired,
                "Ordinary contact movement requires physics, a registration and a MovementBoundary.")
            {
                RequiredCapability = Physics is null ? typeof(IPhysicsWorld)
                : MovementRegistration is null ? typeof(OrdinaryMovementRegistration) : typeof(MovementBoundary)
            };
        return EffectiveMovementBinding.Ordinary(tuning, Physics, MovementRegistration, GroundHeight, GroundNormal,
            MovementBoundary, MovementWaterField, _providedMedium, MovementQueries);
    }

    GroundContactCapabilityException ContactProducerRefusal() => new(
        GroundContactCapabilityRefusal.ContactProducerRestricted, _legacyOnlyContactRefusal!)
    {
        Restriction = _legacyOnlyContactRefusal,
        ProducerKind = _contactProducerKind,
        RequiredCapability = _requiredContactCapability
    };

    /// <summary>The contact frame the next step would capture at the current physics origin. Reading it changes no
    /// context state, so a navigation build captures it once into its snapshot.</summary>
    internal MovementFrameDescriptor NavigationFrame
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Vector3 origin = Physics!.Origin;
            if (MovementQueries is { } queries && (!ReferenceEquals(queries.SourceWorld, Physics) || queries.Origin != origin))
                throw new InvalidOperationException("The movement query source or origin changed during a ground movement step.");
            return ContactFrameAt(origin);
        }
    }

    MoveState StepContact(in MoveState local, Vector2 direction, bool run, float dt)
    {
        CaptureContactFrame();
        var binding = _binding!;
        var scope = binding.CreateOrdinaryScope(_contactFrame);
        EnsureOrigin();
        if (binding.TryAcquire(scope, out var queries) != MovementAvailability.Known)
        {
            EnsureOrigin();
            return MovementStepResult.HoldState(local) with { Position = local.Position + _origin };
        }
        using (queries)
        {
            var result = binding.StepTowards(new(local, _contactFrame, null), direction, run, dt, queries!,
                binding.CaptureOrdinaryBoundary(_contactFrame));
            EnsureOrigin();
            return result.State.State with { Position = result.State.State.Position + _origin };
        }
    }

    MovementMedium FieldMedium(float x, float z, float feetY)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_hasValidatedTuning) throw new InvalidOperationException("Validate tuning before sampling the contact medium.");
        var tuning = _validatedTuning;
        if (tuning.Controller != MovementControllerKind.Contact)
            return _providedMedium?.Invoke(x, z, feetY) ?? MovementMedium.Dry;
        // Keep feet plus half-height as an exact pair. Rounding the centre first can change strict membership.
        var body = new MovementBodyQuery(new(x, feetY, z), tuning.CapsuleRadius,
            tuning.CapsuleHalfHeight, new(MovementRegistration!.WorldId, "ordinary"), null);
        var point = MovementWaterField!.SampleCentreWater(body, new(0, tuning.CapsuleHalfHeight, 0));
        if (!point.IsValid || point.Availability != MovementAvailability.Known || point.Space != body.CurrentSpace)
            throw new InvalidOperationException("The contact pacing water point is unresolved.");
        return point.InWater ? new(point.Interval!.Value.NominalSurfaceY, true, point.SpeedScale) : MovementMedium.Dry;
    }

    /// <summary>Releases only the query selection created by this context. The physics source and borrowed
    /// selections stay caller-owned. Dispose between sequential steps.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        if (_stepping) throw new InvalidOperationException("A ground context cannot be disposed during a step.");
        if (_ownsMovementQueries) MovementQueries!.Dispose();
        _binding?.Dispose();
        _dryContext?.Dispose();
        _disposed = true;
    }
}
