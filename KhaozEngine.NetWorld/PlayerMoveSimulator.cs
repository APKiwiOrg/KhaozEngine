using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.Physics;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The per-tick player movement step, plugged into the shipped prediction/reconciliation seam. The same
/// instance configuration (ground delegate + tuning) drives the authoritative server tick and the client's
/// prediction replay, so they stay in lockstep. Wraps the vertical <see cref="CharacterMovement"/> step.
/// </summary>
public sealed class PlayerMoveSimulator : ITickSimulator<PlayerMoveState, MoveCommand>
{
    private readonly Func<float, float, float> groundHeight;
    private readonly Func<float, float, Vector3>? groundNormal;
    private readonly MoveTuning tuning;
    private readonly IPhysicsWorld? physics;
    private readonly Func<float, float, Vector2>? clampXz;
    private readonly Func<float, float, float, MovementMedium>? medium;

    public PlayerMoveSimulator(Func<float, float, float> groundHeight, MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal = null, WorldBounds? bounds = null, IPhysicsWorld? physics = null,
        Func<float, float, float, MovementMedium>? medium = null)
    {
        this.groundHeight = groundHeight ?? throw new ArgumentNullException(nameof(groundHeight));
        this.tuning = tuning;
        this.groundNormal = groundNormal;
        this.physics = physics;
        // Fold the play-area bound into the step as an XZ clamp, so the vertical axis is resolved at the clamped
        // position (an airborne player is not snapped to the ground at the wall) and the server/client stay identical.
        this.clampXz = bounds is null ? null : bounds.Clamp;
        // Optional fluid-medium provider (x, z, feetY) -> MovementMedium. The GAME supplies the SAME pure delegate on
        // the server and the client so wading (and, on Task 2, swimming) predicts in lockstep. Null = dry land
        // everywhere = bit-identical to the pre-medium simulator.
        this.medium = medium;
    }

    /// <summary>Advances one player by one command over <paramref name="dt"/> seconds: the shared vertical
    /// <see cref="CharacterMovement.Step(in MoveState, in MoveCommand, float, Func{float, float, float}, in MoveTuning, Func{float, float, Vector3}?, IPhysicsWorld?, Func{float, float, Vector2}?, Func{float, float, float, MovementMedium}?)"/>
    /// (gravity + jump + ground contact), resolved against the optional <see cref="IPhysicsWorld"/> (props/buildings),
    /// and clamped into the play area when a <see cref="WorldBounds"/> is set.</summary>
    public PlayerMoveState Step(in PlayerMoveState state, in MoveCommand command, float dt)
    {
        MoveState m = CharacterMovement.Step(state.Move, command, dt, groundHeight, tuning, groundNormal, physics, clampXz, medium);
        // Carry the teleport epoch through unchanged: it is a networking marker, not a movement quantity, so a step
        // only advances position/vertical. This keeps a teleport marker alive across the single-World server's next
        // per-tick step (the sharded head preserves it in-place via PlayerMovementSystem's ref-component write).
        return new PlayerMoveState { Move = m, TeleportEpoch = state.TeleportEpoch };
    }
}
