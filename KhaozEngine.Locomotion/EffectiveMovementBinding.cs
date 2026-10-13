using System;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

public enum EffectiveMovementBindingKind { Legacy = 0, OrdinaryContact = 1, PublicExplicit = 2 }

/// <summary>One resolved controller and query lifetime shared by every movement lifecycle door.</summary>
public sealed partial class EffectiveMovementBinding : IDisposable
{
    readonly MovementEnvironmentContext? _context;
    readonly OrdinaryMovementEnvironment? _ordinary;
    readonly IPhysicsWorldQueryView? _view;
    readonly bool _ownsView;
    readonly ContactComposition _composition;
    bool _disposed;
    public EffectiveMovementBindingKind Kind { get; }
    public MoveTuning Tuning { get; }
    public WaterTraversalPolicy Water { get; }
    public bool UsesLease => Kind != EffectiveMovementBindingKind.Legacy;

    EffectiveMovementBinding(EffectiveMovementBindingKind kind, in MoveTuning tuning, in WaterTraversalPolicy water,
        MovementEnvironmentContext? context = null, OrdinaryMovementEnvironment? ordinary = null,
        IPhysicsWorldQueryView? view = null, bool ownsView = false)
    {
        Kind = kind;
        Tuning = tuning;
        Water = water;
        _context = context;
        _ordinary = ordinary;
        _view = view;
        _ownsView = ownsView;
        _composition = kind == EffectiveMovementBindingKind.OrdinaryContact ?
            ContactComposition.Ordinary(tuning) : ContactComposition.PublicExplicit(tuning);
    }

    public static EffectiveMovementBinding Legacy(in MoveTuning tuning)
    {
        MovementConfigurationValidation.ValidateLegacy(tuning);
        if (tuning.Controller != MovementControllerKind.Legacy)
            throw new ArgumentException("A Legacy binding requires the Legacy controller.", nameof(tuning));
        return new(EffectiveMovementBindingKind.Legacy, tuning, tuning.Water);
    }

    public static EffectiveMovementBinding Ordinary(in MoveTuning tuning, IPhysicsWorld physics,
        OrdinaryMovementRegistration registration, Func<float, float, float> groundHeight,
        Func<float, float, Vector3>? groundNormal, MovementBoundary boundary,
        IMovementWaterField? waterField = null, Func<float, float, float, MovementMedium>? medium = null,
        IPhysicsWorldQueryView? movementQueries = null)
    {
        ArgumentNullException.ThrowIfNull(physics);
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(groundHeight);
        ArgumentNullException.ThrowIfNull(boundary);
        if (tuning.Controller != MovementControllerKind.Contact)
            throw new ArgumentException("An ordinary contact binding requires the Contact controller.", nameof(tuning));
        if (!ReferenceEquals(registration.SourceWorld, physics))
            throw new ArgumentException("The registration must name the configured physical source.", nameof(registration));
        bool owned = movementQueries is null;
        IPhysicsWorldQueryView view = movementQueries ?? physics.CreateQueryViewExcludingStatics(registration.ExclusionSnapshot);
        try
        {
            if (!ReferenceEquals(view.SourceWorld, physics))
                throw new ArgumentException("The movement view must name the configured physical source.", nameof(movementQueries));
            MovementConfigurationValidation.ValidateOrdinaryContact(tuning, view, true, true, medium is not null, waterField is not null);
            var environment = new OrdinaryMovementEnvironment(view, registration, tuning, groundHeight, groundNormal, boundary, waterField);
            var context = new MovementEnvironmentContext(view, environment, environment.Identity);
            return new(EffectiveMovementBindingKind.OrdinaryContact, tuning, tuning.Water, context, environment, view, owned);
        }
        catch { if (owned) view.Dispose(); throw; }
    }

    public static EffectiveMovementBinding PublicExplicit(in MoveTuning tuning, MovementEnvironmentContext environment,
        in WaterTraversalPolicy water)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (tuning.Controller == MovementControllerKind.Contact)
            throw new ArgumentException("A public explicit environment and the Contact selector are two movement selections.", nameof(tuning));
        MovementConfigurationValidation.ValidateExplicit(tuning, water);
        return new(EffectiveMovementBindingKind.PublicExplicit, tuning, water, environment);
    }

    public MovementQueryScope CreateOrdinaryScope(in MovementFrameDescriptor frame)
    {
        AssertUsable();
        return (_ordinary ?? throw new InvalidOperationException("This binding has no ordinary scope.")).CreateScope(frame);
    }

    public MovementAvailability TryAcquire(in MovementQueryScope scope, out MovementQueryLease? queries)
    {
        AssertUsable();
        queries = null;
        return _context is null ? MovementAvailability.Invalid : _context.TryAcquire(scope, out queries);
    }

    public MovementBoundary CaptureOrdinaryBoundary(in MovementFrameDescriptor frame)
    {
        AssertUsable();
        return (_ordinary ?? throw new InvalidOperationException("This binding has no ordinary boundary.")).CaptureBoundary(frame);
    }

    public void Dispose()
    {
        if (_disposed) return;
        // The view's source gate refuses disposal during a read. Retain ownership if that refusal occurs.
        if (_ownsView) _view!.Dispose();
        _disposed = true;
    }

    void AssertUsable() => ObjectDisposedException.ThrowIf(_disposed, this);
}
