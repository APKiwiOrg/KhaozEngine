using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

/// <summary>Resolves NPC ground steering through the shared movement context.</summary>
public static class NpcGroundMovement
{
    /// <summary>Only Following requests travel. The core resolves gravity, support and carried movement state.</summary>
    public static MoveState Step(in MoveState body, in RangeSteering steering, bool run, float dt,
        in MoveTuning tuning, GroundMoveContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Vector2 direction = steering.Status == RangeMoveStatus.Following
            ? steering.WorldDirection : Vector2.Zero;
        return context.Step(body, direction, run, dt, tuning);
    }

    /// <summary>Requests zero input while the core settles support and advances gravity or carried movement.</summary>
    public static MoveState Hold(in MoveState body, float dt, in MoveTuning tuning, GroundMoveContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Step(body, Vector2.Zero, false, dt, tuning);
    }
}
