using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

public sealed partial class MoveToRange
{
    /// <inheritdoc cref="PathFollower.PlanningPending"/>
    public bool PlanningPending => _follower.PlanningPending;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    RangeSteering TickCore(in MoveState body, in MoveTuning tuning, in ReachTarget target,
        float range, bool run, float dt, GroundMoveContext context, PlayerPathInput? playerInput)
        => _follower.UsesPlanning
            ? TickCore<PathFollower.IncrementalPlanning>(body, tuning, target, range, run, dt, context, playerInput)
            : TickCore<PathFollower.SynchronousPlanning>(body, tuning, target, range, run, dt, context, playerInput);

    static RouteStraightener? CreateStraightener(IRegionPathPlanner planner, NavSpace space,
        Func<Vector3, Vector3, bool> allowsSegment, PathFollowConfig? follow, RouteApproachOptions options,
        GroundNavigation? navigation)
    {
        if (!options.StraightenRoutes) return null;
        if (follow?.Planning is null) return new RouteStraightener(planner, space, allowsSegment);
        if (navigation is null)
            throw new ArgumentException("Sliced straightening requires a ground navigation profile.", nameof(follow));
        return new RouteStraightener(navigation);
    }
}
