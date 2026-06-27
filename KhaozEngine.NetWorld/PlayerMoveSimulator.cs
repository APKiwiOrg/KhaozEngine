using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The per-tick player movement step, plugged into the shipped prediction/reconciliation seam. The same
/// instance configuration (ground delegate + tuning) drives the authoritative server tick and the client's
/// prediction replay, so they stay in lockstep. Wraps <see cref="CharacterMovement.Step"/>.
/// </summary>
public sealed class PlayerMoveSimulator : ITickSimulator<PlayerMoveState, MoveCommand>
{
    private readonly Func<float, float, float> groundHeight;
    private readonly Func<float, float, Vector3>? groundNormal;
    private readonly MoveTuning tuning;

    public PlayerMoveSimulator(Func<float, float, float> groundHeight, MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal = null)
    {
        this.groundHeight = groundHeight ?? throw new ArgumentNullException(nameof(groundHeight));
        this.tuning = tuning;
        this.groundNormal = groundNormal;
    }

    /// <summary>Advances one player by one command over <paramref name="dt"/> seconds, ground-clamped.</summary>
    public PlayerMoveState Step(in PlayerMoveState state, in MoveCommand command, float dt) =>
        new() { Position = CharacterMovement.Step(state.Position, command, dt, groundHeight, tuning, groundNormal) };
}
