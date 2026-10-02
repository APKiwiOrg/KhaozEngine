using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Navigation;

public sealed partial class PathFollower
{
    bool _followingRegion;

    /// <summary>
    /// Follows a route to <paramref name="goal"/> using actual feet membership for arrival. The last
    /// Complete waypoint remains active until membership, regardless of AcceptRadius. Intermediate
    /// waypoints retain the configured radius and layer checks. An exhausted Partial route holds at
    /// zero motion until the shared replan cooldown permits another query.
    /// Switching between point and region calls clears the previous route and cooldown. Call
    /// <see cref="Reset"/> when target identity, shape, range or traversal profile changes.
    /// </summary>
    /// <param name="feetPosition">The moving body's actual world feet position.</param>
    /// <param name="goal">The current goal predicate and its conservative anchor and extent.</param>
    /// <param name="agentRadius">Agent radius passed through to the planner on a replan.</param>
    /// <param name="dt">Timestep in seconds.</param>
    /// <exception cref="NotSupportedException">The planner does not implement <see cref="IRegionPathPlanner"/>.</exception>
    public PathFollowOutput Tick(Vector3 feetPosition, NavGoalRegion goal, float agentRadius, float dt)
    {
        ArgumentNullException.ThrowIfNull(goal);
        if (_planner is not IRegionPathPlanner)
            throw new NotSupportedException("Region following requires an IRegionPathPlanner.");
        return TickCore(feetPosition, goal.Anchor, agentRadius, dt, goal);
    }

    PathFollowOutput TickCore(Vector3 position, Vector3 goal, float agentRadius, float dt, NavGoalRegion? region)
    {
        bool followingRegion = region is not null;
        if (_followingRegion != followingRegion)
        {
            Reset();
            _followingRegion = followingRegion;
        }

        Vector2 posXz = new Vector2(position.X, position.Z);
        Vector2 goalXz = new Vector2(goal.X, goal.Z);
        _cooldown = MathF.Max(0f, _cooldown - dt);

        // Point arrival keeps both proximity checks. Region arrival uses only actual feet membership.
        bool arrived = region is not null
            ? region.Contains(position)
            : Vector2.Distance(posXz, goalXz) <= _config.AcceptRadius
                && MathF.Abs(position.Y - goal.Y) <= _config.VerticalAcceptTolerance;
        if (arrived)
        {
            _path = null;
            _index = 0;
            return new PathFollowOutput { WorldDir = Vector2.Zero, State = PathFollowState.Arrived, ActiveWaypoint = Vector2.Zero, HopStart = Vector2.Zero };
        }

        bool needsPlan = _path is null
            || _index >= _path.Waypoints.Count
            || Vector2.Distance(goalXz, _plannedGoalXz) > _config.GoalRetargetTolerance
            || MathF.Abs(goal.Y - _plannedGoalY) > _config.GoalRetargetVerticalTolerance
            || DistanceToActiveCorridor(posXz) > _config.CorridorTolerance;

        if (needsPlan && _cooldown == 0f)
        {
            _path = region is not null
                ? ((IRegionPathPlanner)_planner).FindPath(position, region, agentRadius, _config.Budget)
                : _planner.FindPath(position, goal, agentRadius, _config.Budget);
            _plannedGoalXz = goalXz;
            _plannedGoalY = goal.Y;
            _planOriginXz = posXz;
            _index = 0;
            _cooldown = _config.ReplanCooldownSeconds;
        }

        if (_path is null || _path.Status == NavPathStatus.Unreachable || _path.Waypoints.Count == 0)
        {
            return new PathFollowOutput { WorldDir = Vector2.Zero, State = PathFollowState.Unreachable, ActiveWaypoint = Vector2.Zero, HopStart = Vector2.Zero };
        }

        // Layer membership witnesses climbing. A Complete region endpoint additionally requires the
        // body's actual region membership, so the radius check cannot consume that final waypoint.
        IReadOnlyList<NavWaypoint> waypoints = _path.Waypoints;
        int? agentLayer = _space?.LayerAt(position);
        while (_index < waypoints.Count
            && (!followingRegion || _path.Status != NavPathStatus.Complete || _index < waypoints.Count - 1)
            && Vector2.Distance(posXz, waypoints[_index].Position) <= _config.AcceptRadius
            && (agentLayer is null || waypoints[_index].Layer == agentLayer.Value))
        {
            _index++;
        }

        if (_index >= waypoints.Count)
        {
            if (followingRegion)
            {
                // Keep the exhausted Partial and its index internally so later cooldown ticks can
                // distinguish waiting from an unreachable query. ActivePath hides consumed routes.
                return new PathFollowOutput { WorldDir = Vector2.Zero, State = PathFollowState.WaitingForPath, ActiveWaypoint = Vector2.Zero, HopStart = Vector2.Zero };
            }

            NavPathStatus status = _path.Status;
            _path = null;
            _index = 0;
            if (status == NavPathStatus.Complete)
            {
                return new PathFollowOutput { WorldDir = Vector2.Zero, State = PathFollowState.Arrived, ActiveWaypoint = Vector2.Zero, HopStart = Vector2.Zero };
            }

            // Preserve the point overload's raw-goal steering on Partial exhaustion.
            Vector2 towardGoal = Vector2.Normalize(goalXz - posXz);
            return new PathFollowOutput { WorldDir = towardGoal, State = PathFollowState.Following, ActiveWaypoint = Vector2.Zero, HopStart = Vector2.Zero };
        }

        NavWaypoint active = waypoints[_index];
        if (active.Kind == NavWaypointKind.Hop)
        {
            Vector2 hopStart = _index == 0 ? _planOriginXz : waypoints[_index - 1].Position;
            return new PathFollowOutput { WorldDir = Vector2.Zero, State = PathFollowState.Hopping, ActiveWaypoint = active.Position, HopStart = hopStart };
        }

        Vector2 offset = active.Position - posXz;
        Vector2 dir = followingRegion && offset == Vector2.Zero ? Vector2.Zero : Vector2.Normalize(offset);
        return new PathFollowOutput { WorldDir = dir, State = PathFollowState.Following, ActiveWaypoint = active.Position, HopStart = Vector2.Zero };
    }
}
