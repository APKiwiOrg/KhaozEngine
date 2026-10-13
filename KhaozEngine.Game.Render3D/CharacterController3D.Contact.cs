using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;

namespace KhaozEngine.Game;

public sealed partial class CharacterController3D
{
    /// <summary>Ordinary movement selection. Legacy preserves released motion.</summary>
    public MovementControllerKind Controller = MovementControllerKind.Legacy;
    /// <summary>The contact foot disc radius as a fraction of the capsule radius, in (0, 1].</summary>
    public float FootRadiusFraction = 0.5f;
    /// <summary>The certified contact water traversal policy. Contact requires a non-Legacy mode.</summary>
    public WaterTraversalPolicy Water = default;

    internal MoveState MovementState => _state;
    EffectiveMovementBinding? _binding;
    IPhysicsWorld? _boundPhysics;
    IMovementWaterField? _boundField;
    OrdinaryMovementRegistration? _boundRegistration;
    MovementBoundary? _boundBoundary;
    Func<float, float, float>? _boundHeight;
    Func<float, float, Vector3>? _boundNormal;
    Func<float, float, float, MovementMedium>? _boundMedium;
    MovementFrameDescriptor _contactFrame;
    bool _frameCaptured;
    bool _disposed;
    bool _updating;

    void ConfigureBinding(in MoveTuning tuning, Func<float, float, float> height,
        Func<float, float, Vector3>? normal, IPhysicsWorld? physics,
        Func<float, float, float, MovementMedium>? medium, IMovementWaterField? field,
        OrdinaryMovementRegistration? registration, MovementBoundary? boundary)
    {
        if (_binding is not null && _binding.Tuning == tuning && ReferenceEquals(_boundPhysics, physics) &&
            ReferenceEquals(_boundField, field) && ReferenceEquals(_boundRegistration, registration) &&
            ReferenceEquals(_boundBoundary, boundary) && ReferenceEquals(_boundHeight, height) &&
            ReferenceEquals(_boundNormal, normal) && ReferenceEquals(_boundMedium, medium)) return;
        EffectiveMovementBinding next;
        if (tuning.Controller == MovementControllerKind.Contact)
        {
            EffectiveMovementBinding.Preflight(tuning, EffectiveMovementBindingKind.OrdinaryContact);
            if (physics is null || registration is null || boundary is null)
                throw new ArgumentException("Ordinary contact movement requires physics, a registration and a MovementBoundary.");
            next = EffectiveMovementBinding.Ordinary(tuning, physics, registration, height, normal, boundary, field, medium);
        }
        else next = EffectiveMovementBinding.Legacy(tuning);
        try { _binding?.Dispose(); }
        catch { next.Dispose(); throw; }
        _binding = next;
        _boundPhysics = physics;
        _boundField = field;
        _boundRegistration = registration;
        _boundBoundary = boundary;
        _boundHeight = height;
        _boundNormal = normal;
        _boundMedium = medium;
    }

    void StepContact(in MoveCommand command, float dt, IPhysicsWorld physics)
    {
        Vector3 origin = physics.Origin;
        if (!_frameCaptured || _contactFrame.PhysicsOrigin != origin)
        {
            _contactFrame = new(default, origin, _frameCaptured ? checked(_contactFrame.Epoch + 1) : 1);
            _frameCaptured = true;
        }
        var binding = _binding!;
        var scope = binding.CreateOrdinaryScope(_contactFrame);
        if (binding.TryAcquire(scope, out var queries) != MovementAvailability.Known)
        {
            _state = MovementStepResult.HoldState(_state);
            return;
        }
        using (queries)
        {
            var local = _state with { Position = _state.Position - origin };
            var result = binding.Step(new(local, _contactFrame, null), command, dt, queries!,
                binding.CaptureOrdinaryBoundary(_contactFrame));
            queries!.AssertCurrent();
            _state = result.State.State with { Position = result.State.State.Position + origin };
        }
    }

    /// <summary>Releases the engine-created movement view. The supplied physics world remains caller-owned.
    /// Dispose between updates.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        if (_updating) throw new InvalidOperationException("A character controller cannot be disposed during an update.");
        _binding?.Dispose();
        _disposed = true;
    }
}
