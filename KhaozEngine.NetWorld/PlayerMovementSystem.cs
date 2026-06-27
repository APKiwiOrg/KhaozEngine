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
/// <see cref="ReplicatedPosition"/> via the shared <see cref="CharacterMovement.Step"/> (the same step the
/// single-<see cref="World"/> <see cref="WorldServer"/> and the client's prediction run, so they stay in
/// lockstep). Read-only <see cref="Ghost"/>s and in-flight <see cref="Migrating"/> entities are skipped: the
/// owning cell is the sole simulator. Stateless - one instance is shared across all cells.
/// </summary>
public sealed class PlayerMovementSystem : ISystem
{
    private readonly Func<float, float, float> groundHeight;
    private readonly Func<float, float, Vector3>? groundNormal;
    private readonly MoveTuning tuning;
    private readonly WorldBounds? bounds;
    private readonly WorldColliders? colliders;

    public PlayerMovementSystem(Func<float, float, float> groundHeight, MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal = null, WorldBounds? bounds = null, WorldColliders? colliders = null)
    {
        this.groundHeight = groundHeight ?? throw new ArgumentNullException(nameof(groundHeight));
        this.tuning = tuning;
        this.groundNormal = groundNormal;
        this.bounds = bounds;
        this.colliders = colliders;
    }

    public void Update(World world, float dt)
    {
        world.ForEach<NetId, ReplicatedPosition, PendingMove>((Entity e, ref NetId _, ref ReplicatedPosition pos, ref PendingMove move) =>
        {
            if (world.Has<Ghost>(e) || world.Has<Migrating>(e)) return;   // owner is the only simulator
            Vector3 p = CharacterMovement.Step(pos.Value, move.Command, dt, groundHeight, tuning, groundNormal, colliders);
            if (bounds is not null)
            {
                Vector2 c = bounds.Clamp(p.X, p.Z);
                p = new Vector3(c.X, groundHeight(c.X, c.Y) + tuning.CapsuleHalfHeight, c.Y);
            }
            pos.Value = p;
        });
    }
}
