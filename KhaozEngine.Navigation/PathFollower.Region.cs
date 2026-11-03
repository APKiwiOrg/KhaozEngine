using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Navigation;

public sealed partial class PathFollower
{
    bool _followingRegion;
    // A query already terminal at its start has no new corridor to exhaust. It keeps the ordinary cooldown.
    bool _freshRegionTerminal;

    /// <summary>
    /// Follows a route to <paramref name="goal"/> using actual feet membership for arrival. The last
    /// Complete walk waypoint remains active until membership, regardless of AcceptRadius. Intermediate
    /// waypoints retain the configured radius and layer checks. An exhausted Partial route holds at
    /// zero motion until the shared replan cooldown permits another query. Reaching the retained
    /// Complete endpoint outside the region also makes a replan due, even for anchor drift within
    /// tolerance. A corridor not terminal when queried refreshes immediately when its terminal is
    /// reached under the configured waypoint radius and layer checks. Exact coordinate equality is
    /// not required, since movement can round differently from the grid's cell centres.
    /// A fresh query already terminal at its start keeps the ordinary cooldown until it leaves that state.
    /// A Drop locks traversal before these checks. Supported acknowledgement of a final Complete Drop
    /// is also a reached-terminal witness, so a stale route can refresh on the next tick before cooldown.
    /// Before the lock nothing is committed, so membership arrives even with a Drop still pending. A due
    /// replan is immediate when the next transition is an unlocked Drop, so its lock starts from the current
    /// route. A Drop next on the refreshed route locks in that tick under the ordinary cooldown.
    /// Existing region waypoints advance before this decision so an eligible exhausted
    /// Partial replans in the same tick. Each tick queries at most once. A freshly exhausted Partial
    /// with no cooldown returns Unreachable at zero motion and retries on the next tick.
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

    PathFollowOutput TickCore(Vector3 position, Vector3 goal, float agentRadius, float dt, NavGoalRegion? region,
        bool preparingDrop = false)
    {
        if (_drop is not null && !preparingDrop)
        {
            AdvanceDropTime(dt);
            return DropOutput();
        }
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
        // An unlocked Drop commits nothing, so membership still arrives before it. Point proximity
        // cannot tell a ledge above the goal from the goal, so a pending Drop keeps owning that route.
        bool arrived = region is not null
            ? region.Contains(position)
            : Vector2.Distance(posXz, goalXz) <= _config.AcceptRadius
                && MathF.Abs(position.Y - goal.Y) <= _config.VerticalAcceptTolerance;
        if (arrived && (followingRegion || !HasPendingDrop()))
            return Arrive();

        // A reached Complete endpoint belongs only to the point intent that was queried. Retain it
        // until that check so obsolete endpoints can refresh through the single query site below.
        // Point Partial consumption stays after query eligibility, preserving its raw-goal tick.
        bool pointIntentMatches = goalXz == _plannedGoalXz && goal.Y == _plannedGoalY;
        bool completeAtTerminal = (followingRegion || _path?.Status == NavPathStatus.Complete)
            && AdvanceReachedWaypoints(position, posXz, retainCompleteTerminal: true);
        if (!completeAtTerminal) _freshRegionTerminal = false;
        if (!followingRegion && completeAtTerminal && pointIntentMatches)
            return Arrive();

        bool needsPlan = _path is null
            || _index >= _path.Waypoints.Count
            || Vector2.Distance(goalXz, _plannedGoalXz) > _config.GoalRetargetTolerance
            || MathF.Abs(goal.Y - _plannedGoalY) > _config.GoalRetargetVerticalTolerance
            || completeAtTerminal
            || DistanceToActiveCorridor(posXz) > _config.CorridorTolerance;

        // A useful corridor reaching an obsolete terminal needs one immediate refresh. A fresh
        // terminal region result stays protected. Point completion clears its route and keeps cooldown.
        // A due query is also immediate before an unlocked one-way Drop, which must never launch on an
        // obsolete route. The refreshed route locks any Drop that is next in this same tick.
        bool staleCompleteTerminal = completeAtTerminal && !_freshRegionTerminal;
        bool replanned = needsPlan && (_cooldown == 0f || staleCompleteTerminal || DropIsNext(position, posXz));
        if (replanned)
        {
            _path = region is not null
                ? ((IRegionPathPlanner)_planner).FindPath(position, region, agentRadius, _config.Budget)
                : _planner.FindPath(position, goal, agentRadius, _config.Budget);
            _freshRegionTerminal = false;
            _plannedGoalXz = goalXz;
            _plannedGoalY = goal.Y;
            pointIntentMatches = true;
            _planOriginXz = posXz;
            _index = 0;
            _cooldown = _config.ReplanCooldownSeconds;
            _drop = null;
        }

        // Supported preparation keeps the existing endpoints and preview unless the goal actually refreshed.
        if (_drop is not null) return DropOutput();

        if (_path is null || _path.Status == NavPathStatus.Unreachable || _path.Waypoints.Count == 0)
        {
            // No current point result exists when cooldown defers a query after completion or
            // retargeting a failed query. That wait is not a planner failure for the current intent.
            PathFollowState state = !followingRegion && !replanned && (_path is null || !pointIntentMatches)
                ? PathFollowState.WaitingForPath : PathFollowState.Unreachable;
            return new PathFollowOutput { WorldDir = Vector2.Zero, State = state, ActiveWaypoint = Vector2.Zero, HopStart = Vector2.Zero };
        }

        if (!followingRegion || replanned)
        {
            bool terminalAfterPlan = AdvanceReachedWaypoints(position, posXz, followingRegion);
            _freshRegionTerminal = followingRegion && terminalAfterPlan;
        }
        IReadOnlyList<NavWaypoint> waypoints = _path.Waypoints;
        if (_index >= waypoints.Count)
        {
            if (followingRegion)
            {
                // Keep the exhausted Partial and its index internally so later cooldown ticks can
                // distinguish waiting from an unreachable query. ActivePath hides consumed routes.
                PathFollowState state = _cooldown > 0f ? PathFollowState.WaitingForPath : PathFollowState.Unreachable;
                return new PathFollowOutput { WorldDir = Vector2.Zero, State = state, ActiveWaypoint = Vector2.Zero, HopStart = Vector2.Zero };
            }

            NavPathStatus status = _path.Status;
            _path = null;
            _index = 0;
            if (status == NavPathStatus.Complete)
            {
                return pointIntentMatches ? Arrive()
                    : new PathFollowOutput { WorldDir = Vector2.Zero, State = PathFollowState.WaitingForPath, ActiveWaypoint = Vector2.Zero, HopStart = Vector2.Zero };
            }

            // Preserve the point overload's raw-goal steering on Partial exhaustion.
            Vector2 towardGoal = Vector2.Normalize(goalXz - posXz);
            return new PathFollowOutput { WorldDir = towardGoal, State = PathFollowState.Following, ActiveWaypoint = Vector2.Zero, HopStart = Vector2.Zero };
        }

        if (TryDrop(out PathFollowOutput dropping)) return dropping;
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

    PathFollowOutput Arrive()
    {
        _drop = null;
        _path = null;
        _index = 0;
        _freshRegionTerminal = false;
        return new PathFollowOutput { WorldDir = Vector2.Zero, State = PathFollowState.Arrived, ActiveWaypoint = Vector2.Zero, HopStart = Vector2.Zero };
    }

    // Returns true at a reached Complete endpoint without consuming it when requested, so arrival
    // can validate current region membership or the point intent before retiring the route. A final
    // Drop's supported acknowledgement preserves that witness after consuming its landing marker.
    bool AdvanceReachedWaypoints(Vector3 position, Vector2 posXz, bool retainCompleteTerminal)
    {
        if (_path is null || _path.Status == NavPathStatus.Unreachable)
            return false;

        IReadOnlyList<NavWaypoint> waypoints = _path.Waypoints;
        int? agentLayer = _space?.LayerAt(position);
        while (_index < waypoints.Count && waypoints[_index].Kind != NavWaypointKind.Drop
            && Reached(_index, posXz, agentLayer))
        {
            if (retainCompleteTerminal && _path.Status == NavPathStatus.Complete && _index == waypoints.Count - 1)
                return true;
            _index++;
        }

        return retainCompleteTerminal && _path.Status == NavPathStatus.Complete
            && _index == waypoints.Count && _index > 0
            && waypoints[_index - 1].Kind == NavWaypointKind.Drop;
    }

    // Whether the waypoint at index is reached under the configured radius, layer and passed-collinear checks.
    bool Reached(int index, Vector2 posXz, int? agentLayer)
    {
        NavWaypoint waypoint = _path!.Waypoints[index];
        return (Vector2.Distance(posXz, waypoint.Position) <= _config.AcceptRadius
                && (agentLayer is null || waypoint.Layer == agentLayer.Value))
            || (_config.ConsumePassedCollinearWaypoints && PassedWaypoint(index, posXz, agentLayer));
    }
}
