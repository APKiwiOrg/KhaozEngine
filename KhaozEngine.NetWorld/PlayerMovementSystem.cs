using System;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The per-cell authoritative movement step. Added to every <see cref="KhaozEngine.Sharding.CellSim"/>'s
/// <see cref="World"/> by <see cref="ShardedWorldServer"/>, so <see cref="KhaozEngine.Sharding.ShardHost.Tick"/>
/// runs it for every cell (fanned across the opt-in scheduler - cells are disjoint worlds, so the result is
/// scheduler-independent). For each owned entity carrying a <see cref="PendingMove"/> it advances the
/// <see cref="ReplicatedPosition"/> + <see cref="MovementState"/> (the vertical axis) via the shared
/// <see cref="CharacterMovement.Step(in MoveState, in MoveCommand, float, Func{float, float, float}, in MoveTuning, Func{float, float, Vector3}?, IPhysicsWorld?, Func{float, float, Vector2}?, Func{float, float, float, MovementMedium}?)"/>
/// (the same step the single-<see cref="World"/> <see cref="WorldServer"/> and the client's prediction run, so
/// they stay in lockstep). <see cref="MovementState"/> is required on every movable entity (added at spawn,
/// carried across handoff because it is replicated). Read-only <see cref="Ghost"/>s and in-flight
/// <see cref="Migrating"/> entities are skipped: the owning cell is the sole simulator. Stateless - one instance
/// is shared across all cells (no mutable fields, so it is safe to fan across the scheduler).
/// </summary>
public sealed class PlayerMovementSystem : ISystem
{
    private readonly Func<float, float, float> groundHeight;
    private readonly Func<float, float, Vector3>? groundNormal;
    private readonly MoveTuning tuning;
    private readonly IPhysicsWorld? physics;
    private readonly Func<float, float, Vector2>? clampXz;
    private readonly Func<float, float, float, MovementMedium>? medium;

    public PlayerMovementSystem(Func<float, float, float> groundHeight, MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal = null, WorldBounds? bounds = null, IPhysicsWorld? physics = null,
        Func<float, float, float, MovementMedium>? medium = null)
    {
        this.groundHeight = groundHeight ?? throw new ArgumentNullException(nameof(groundHeight));
        this.tuning = tuning;
        this.groundNormal = groundNormal;
        this.physics = physics;
        this.clampXz = bounds is null ? null : bounds.Clamp;   // play-area bound folded into the step (XZ only)
        // Optional fluid-medium provider, mirrored from the authoritative server so every cell wades identically to
        // the client's prediction. Null = dry land everywhere = bit-identical to the pre-medium system.
        this.medium = medium;
    }

    public void Update(World world, float dt)
    {
        world.ForEach<NetId, ReplicatedPosition, PendingMove, MovementState>(
            (Entity e, ref NetId _, ref ReplicatedPosition pos, ref PendingMove move, ref MovementState ms) =>
        {
            if (world.Has<Ghost>(e) || world.Has<Migrating>(e)) return;   // owner is the only simulator

            var state = new MoveState
            {
                Position = pos.Value,
                VerticalVelocity = ms.VerticalVelocity,
                Grounded = ms.Grounded,
                TimeSinceGrounded = ms.TimeSinceGrounded,
                JumpBufferRemaining = ms.JumpBufferRemaining,
                Swimming = ms.Swimming,   // carry the swim flag IN so the enter/exit hysteresis band works across ticks
                ClimbRateEwma = ms.ClimbRateEwma,   // carry the sim-local ascent EWMA IN so the exported signal converges
                // Carry the server-authored haste/slow multiplier IN. It is a movement INPUT, so unlike the fields
                // below it is never written back OUT: the step does not derive it, SetSpeedScale is its only author,
                // and re-quantizing an already-quantized value every tick would only invite drift.
                SpeedScale = MovementState.DecodeSpeedScale(ms.SpeedScaleQ),
            };
            state = CharacterMovement.Step(state, move.Command, dt, groundHeight, tuning, groundNormal, physics, clampXz, medium);

            pos.Value = state.Position;
            ms.VerticalVelocity = state.VerticalVelocity;
            ms.Grounded = state.Grounded;
            ms.TimeSinceGrounded = state.TimeSinceGrounded;
            ms.JumpBufferRemaining = state.JumpBufferRemaining;
            ms.Swimming = state.Swimming;   // write the swim flag back OUT so it replicates (TryGetPlayerState + remotes)
            ms.ClimbRateEwma = state.ClimbRateEwma;   // persist the sim-local ascent EWMA tick-to-tick (rides no wire)
            // Write the quantized step-climb rate OUT so it replicates to remotes (the glide signal). The single-World
            // WorldServer does this via MovementState.From per tick; the sharded per-cell step must do it here or a remote
            // on a sharded server never sees a climb (ClimbRateQ stays at its spawn value of 0).
            ms.ClimbRateQ = MovementState.QuantizeClimbRate(state.ClimbRate);
        });
    }
}
