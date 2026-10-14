using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;

namespace KhaozEngine.NetWorld;

public sealed partial class PlayerMoveSimulator
{
    readonly bool ownsBinding;
    bool disposed;
    bool ordinaryFrameCaptured;
    MovementFrameDescriptor ordinaryFrame;
    internal EffectiveMovementBinding Binding { get; }

    public PlayerMoveSimulator(Func<float, float, float> groundHeight, MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal, WorldBounds? bounds, IPhysicsWorld? physics,
        Func<float, float, float, MovementMedium>? medium, SamplerSpace samplerSpace,
        ExplicitPlayerMovement? explicitMovement, IMovementWaterField? movementWaterField,
        OrdinaryMovementRegistration? movementRegistration, MovementBoundary? movementBoundary)
        : this(groundHeight, tuning, groundNormal, bounds, physics, medium, samplerSpace,
            explicitMovement, movementWaterField, movementRegistration, movementBoundary, null)
    { }

    internal PlayerMoveSimulator(Func<float, float, float> groundHeight, MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal, WorldBounds? bounds, IPhysicsWorld? physics,
        Func<float, float, float, MovementMedium>? medium, SamplerSpace samplerSpace,
        EffectiveMovementBinding binding, ExplicitPlayerMovement? publicExplicit)
        : this(groundHeight, tuning, groundNormal, bounds, physics, medium, samplerSpace,
            publicExplicit, null, null, null, binding)
    { }

    MovementFrameDescriptor OrdinaryFrame()
    {
        Vector3 origin = physics!.Origin;
        if (!ordinaryFrameCaptured || ordinaryFrame.Frame != Frame || ordinaryFrame.PhysicsOrigin != origin)
        {
            ordinaryFrame = new(Frame, origin, ordinaryFrameCaptured ? checked(ordinaryFrame.Epoch + 1) : 1);
            ordinaryFrameCaptured = true;
        }
        return ordinaryFrame;
    }

    internal MovementStepResult SettlePlacement(in PlayerMoveState state, PlayerMoveReadScope read)
    {
        read.AssertUsable();
        var queries = read.Queries ?? throw new InvalidOperationException("Placement requires a live movement read.");
        var result = Binding.SettlePlacement(new(OrdinaryPlayerMovement.ToPhysics(state.Move, queries.Frame), queries.Frame, null),
            queries, read.Boundary);
        return new(new(OrdinaryPlayerMovement.FromPhysics(result.State.State, queries.Frame), result.State.Frame,
            result.State.Selection), result.Outcome);
    }

    public void Dispose()
    {
        lock (frameMutationGate)
        {
            if (disposed) return;
            if (System.Threading.Volatile.Read(ref explicitReadActive) != 0)
                throw new InvalidOperationException("A movement simulator cannot be disposed during a read or its preparation.");
            if (ownsBinding) Binding.Dispose();
            disposed = true;
        }
    }
}
