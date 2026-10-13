using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Sharding;

namespace KhaozEngine.NetWorld;

public sealed partial class WorldServerConfig
{
    public IMovementWaterField? MovementWaterField { get; init; }
    public OrdinaryMovementRegistration? MovementRegistration { get; init; }
    /// <summary>Absolute playable boundary. Takes precedence over WorldBounds for ordinary contact movement.</summary>
    public MovementBoundary? MovementBoundary { get; init; }
}

public sealed partial class WorldClientConfig
{
    public IMovementWaterField? MovementWaterField { get; init; }
    public OrdinaryMovementRegistration? MovementRegistration { get; init; }
    /// <summary>Absolute playable boundary. Takes precedence over WorldBounds for ordinary contact movement.</summary>
    public MovementBoundary? MovementBoundary { get; init; }
}

public sealed partial class ShardedWorldServerConfig
{
    public Func<CellSim, IMovementWaterField?>? MovementWaterFieldFactory { get; init; }
    public Func<CellSim, OrdinaryMovementRegistration>? MovementRegistrationFactory { get; init; }
    public Func<CellSim, MovementBoundary>? MovementBoundaryFactory { get; init; }
}

internal static class OrdinaryPlayerMovement
{
    internal static EffectiveMovementBinding Resolve(Func<float, float, float> groundHeight, MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal, WorldBounds? bounds, IPhysicsWorld? physics,
        Func<float, float, float, MovementMedium>? medium, SamplerSpace samplerSpace,
        ExplicitPlayerMovement? publicExplicit, IMovementWaterField? field,
        OrdinaryMovementRegistration? registration, MovementBoundary? boundary)
    {
        if (publicExplicit is not null)
            return EffectiveMovementBinding.PublicExplicit(tuning, publicExplicit.Environment, publicExplicit.Water);
        if (tuning.Controller == MovementControllerKind.Legacy) return EffectiveMovementBinding.Legacy(tuning);
        if (physics is null || registration is null)
            throw new ArgumentException("Ordinary contact movement requires physics and a movement registration.");
        if (boundary is null && bounds?.TryCaptureExplicit(Vector3.Zero, out boundary) != MovementAvailability.Known)
            throw new ArgumentException("Ordinary contact movement requires an absolute MovementBoundary or certified WorldBounds.");
        var groundSpace = samplerSpace == SamplerSpace.Frame ? OrdinaryGroundSpace.Frame : OrdinaryGroundSpace.World;
        return EffectiveMovementBinding.Ordinary(tuning, physics, registration, groundHeight, groundNormal,
            boundary!, field, medium, groundSpace: groundSpace);
    }

    internal static void ValidateSharded(ShardedWorldServerConfig config, MoveTuning tuning,
        WorldBounds? bounds, bool hasMedium)
    {
        if (config.ExplicitMovementFactory is not null)
        {
            EffectiveMovementBinding.Preflight(tuning, EffectiveMovementBindingKind.PublicExplicit);
            return;
        }
        if (tuning.Controller == MovementControllerKind.Legacy)
        {
            EffectiveMovementBinding.Preflight(tuning, EffectiveMovementBindingKind.Legacy);
            return;
        }
        EffectiveMovementBinding.Preflight(tuning, EffectiveMovementBindingKind.OrdinaryContact);
        if (config.PhysicsWorldFactory is null || config.MovementRegistrationFactory is null ||
            config.MovementBoundaryFactory is null && bounds?.TryCaptureExplicit(Vector3.Zero, out _) != MovementAvailability.Known ||
            hasMedium && config.MovementWaterFieldFactory is null)
            throw new ArgumentException("Contact cells require physics, registration and boundary factories, and a field for a medium delegate.", nameof(config));
    }

    internal static MoveState ToPhysics(in MoveState state, in MovementFrameDescriptor frame)
    {
        MoveState local = state;
        local.Position -= frame.PhysicsOrigin - frame.Frame.Anchor;
        return local;
    }

    internal static MoveState FromPhysics(in MoveState state, in MovementFrameDescriptor frame)
    {
        MoveState planar = state;
        planar.Position += frame.PhysicsOrigin - frame.Frame.Anchor;
        return planar;
    }
}
