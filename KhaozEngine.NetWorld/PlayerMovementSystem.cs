using System;
using System.Numerics;
using KhaozEngine.Collision;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The per-cell authoritative movement step. Added to every <see cref="KhaozEngine.Sharding.CellSim"/>'s
/// <see cref="World"/> by <see cref="ShardedWorldServer"/>, so <see cref="KhaozEngine.Sharding.ShardHost.Tick"/>
/// runs it for every cell (fanned across the opt-in scheduler - cells are disjoint worlds, so the result is
/// scheduler-independent). For each owned entity carrying a <see cref="PendingMove"/> it advances the
/// <see cref="ReplicatedPosition"/> + <see cref="MovementState"/> (the vertical axis) via the shared
/// <see cref="CharacterMovement.Step(in MoveState, in MoveCommand, float, Func{float, float, float}, in MoveTuning, Func{float, float, Vector3}?, WorldColliders?, Func{float, float, Vector2}?)"/>
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
    private readonly WorldColliders? colliders;
    private readonly WorldSurfaces? surfaces;
    private readonly Func<float, float, Vector2>? clampXz;

    public PlayerMovementSystem(Func<float, float, float> groundHeight, MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal = null, WorldBounds? bounds = null, WorldColliders? colliders = null,
        WorldSurfaces? surfaces = null)
    {
        this.groundHeight = groundHeight ?? throw new ArgumentNullException(nameof(groundHeight));
        this.tuning = tuning;
        this.groundNormal = groundNormal;
        this.colliders = colliders;
        this.surfaces = surfaces;
        this.clampXz = bounds is null ? null : bounds.Clamp;   // play-area bound folded into the step (XZ only)
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
            };
            state = CharacterMovement.Step(state, move.Command, dt, groundHeight, tuning, groundNormal, colliders, clampXz, surfaces);

            pos.Value = state.Position;
            ms.VerticalVelocity = state.VerticalVelocity;
            ms.Grounded = state.Grounded;
            ms.TimeSinceGrounded = state.TimeSinceGrounded;
            ms.JumpBufferRemaining = state.JumpBufferRemaining;
        });
    }
}
