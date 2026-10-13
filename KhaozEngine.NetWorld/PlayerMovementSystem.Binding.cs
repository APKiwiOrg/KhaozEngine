using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;

namespace KhaozEngine.NetWorld;

public sealed partial class PlayerMovementSystem
{
    readonly bool ownsBinding;
    bool disposed;
    internal EffectiveMovementBinding Binding { get; }

    public PlayerMovementSystem(Func<float, float, float> groundHeight, MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal, WorldBounds? bounds, IPhysicsWorld? physics,
        Func<float, float, float, MovementMedium>? medium, WorldFrame frame, SamplerSpace samplerSpace,
        ExplicitPlayerMovement? explicitMovement, IMovementWaterField? movementWaterField,
        OrdinaryMovementRegistration? movementRegistration, MovementBoundary? movementBoundary)
        : this(groundHeight, tuning, groundNormal, bounds, physics, medium, frame, samplerSpace,
            explicitMovement, movementWaterField, movementRegistration, movementBoundary, null)
    { }

    internal PlayerMovementSystem(Func<float, float, float> groundHeight, MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal, WorldBounds? bounds, IPhysicsWorld? physics,
        Func<float, float, float, MovementMedium>? medium, WorldFrame frame, SamplerSpace samplerSpace,
        EffectiveMovementBinding binding, ExplicitPlayerMovement? publicExplicit)
        : this(groundHeight, tuning, groundNormal, bounds, physics, medium, frame, samplerSpace,
            publicExplicit, null, null, null, binding)
    { }

    public void Dispose()
    {
        if (disposed) return;
        explicitSimulator?.Dispose();
        if (ownsBinding) Binding.Dispose();
        disposed = true;
    }
}
