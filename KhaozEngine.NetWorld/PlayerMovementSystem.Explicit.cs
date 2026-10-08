using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;

namespace KhaozEngine.NetWorld;

public sealed partial class PlayerMovementSystem
{
    readonly PlayerMoveSimulator? explicitSimulator;

    /// <summary>Builds a cell-local explicit simulator. Its lease remains held through every ECS
    /// result write. Each cell owns a distinct simulator and physics view.</summary>
    public PlayerMovementSystem(Func<float, float, float> groundHeight, MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal, WorldBounds? bounds, IPhysicsWorld? physics,
        Func<float, float, float, MovementMedium>? medium, WorldFrame frame, SamplerSpace samplerSpace,
        ExplicitPlayerMovement? explicitMovement)
        : this(groundHeight, tuning, groundNormal, bounds, physics, medium, frame, samplerSpace)
    {
        if (explicitMovement is not null)
            explicitSimulator = new(groundHeight, tuning, groundNormal, bounds, physics, medium,
                samplerSpace, explicitMovement)
            { Frame = frame };
    }

    static void PrepareExplicitOwners(World world)
    {
        // Adding a missing component during ForEach is structural. Seed it before acquiring any
        // read so the actual carried timer result can be published inside that entity's lease.
        List<Entity>? missing = null;
        world.ForEach<NetId, ReplicatedPosition, PendingMove, MovementState>(
            (Entity entity, ref NetId _, ref ReplicatedPosition _, ref PendingMove _, ref MovementState _) =>
            {
                if (!world.Has<Ghost>(entity) && !world.Has<Migrating>(entity) && !world.Has<MovementOwnerState>(entity))
                    (missing ??= []).Add(entity);
            });
        if (missing is null) return;
        foreach (Entity entity in missing) world.Set(entity, default(MovementOwnerState));
    }
}
