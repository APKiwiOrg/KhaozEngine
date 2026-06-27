using System;
using System.Numerics;
using KhaozEngine.Collision;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;

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
    private readonly WorldColliders? colliders;
    private readonly Func<float, float, Vector2>? clampXz;

    public PlayerMoveSimulator(Func<float, float, float> groundHeight, MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal = null, WorldBounds? bounds = null, WorldColliders? colliders = null)
    {
        this.groundHeight = groundHeight ?? throw new ArgumentNullException(nameof(groundHeight));
        this.tuning = tuning;
        this.groundNormal = groundNormal;
        this.colliders = colliders;
        // Fold the play-area bound into the step as an XZ clamp, so the vertical axis is resolved at the clamped
        // position (an airborne player is not snapped to the ground at the wall) and the server/client stay identical.
        this.clampXz = bounds is null ? null : bounds.Clamp;
    }

    /// <summary>Advances one player by one command over <paramref name="dt"/> seconds: the shared vertical
    /// <see cref="CharacterMovement.Step(in MoveState, in MoveCommand, float, Func{float, float, float}, in MoveTuning, Func{float, float, Vector3}?, WorldColliders?, Func{float, float, Vector2}?)"/>
    /// (gravity + jump + ground contact), pushed out of any static <see cref="WorldColliders"/> (props/buildings),
    /// and clamped into the play area when a <see cref="WorldBounds"/> is set.</summary>
    public PlayerMoveState Step(in PlayerMoveState state, in MoveCommand command, float dt)
    {
        MoveState m = CharacterMovement.Step(state.Move, command, dt, groundHeight, tuning, groundNormal, colliders, clampXz);
        return new PlayerMoveState { Move = m };
    }
}
