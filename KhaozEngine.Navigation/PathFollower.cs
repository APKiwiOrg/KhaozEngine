using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Navigation;

/// <summary>
/// Where a <see cref="PathFollower"/> stands relative to its goal after a <see cref="PathFollower.Tick"/>.
/// </summary>
public enum PathFollowState
{
    /// <summary>Steering toward the active waypoint or the raw goal. <see cref="PathFollowOutput.WorldDir"/>
    /// is a unit vector.</summary>
    Following,

    /// <summary>The goal has been reached (within <see cref="PathFollowConfig.AcceptRadius"/>). The
    /// stored path was cleared.</summary>
    Arrived,

    /// <summary>The planner could not find a route to the goal. The follower keeps retrying, gated by
    /// <see cref="PathFollowConfig.ReplanCooldownSeconds"/>, in case the world changes.</summary>
    Unreachable,

    /// <summary>Steering toward a hop link's landing. Ground steering is suspended
    /// (<see cref="PathFollowOutput.WorldDir"/> is zero) while the consumer drives its own lunge motion from
    /// <see cref="PathFollowOutput.HopStart"/> to <see cref="PathFollowOutput.ActiveWaypoint"/>. The follower
    /// resumes <see cref="Following"/> (or <see cref="Arrived"/>) once the agent reaches the landing.</summary>
    Hopping,
}

/// <summary>
/// Per-tick steering result from <see cref="PathFollower.Tick"/>. This is the raw follow direction only:
/// a dynamic-avoidance layer (steering around other agents or late-appearing obstacles) is expected to
/// run after the follower and before <c>CharacterMovement.StepTowards</c>, adjusting
/// <see cref="WorldDir"/> without touching the follower's own path state.
/// </summary>
public readonly struct PathFollowOutput
{
    /// <summary>Desired horizontal travel direction in world space (XZ), unit length while
    /// <see cref="State"/> is <see cref="PathFollowState.Following"/> and zero otherwise. Feeds
    /// <c>CharacterMovement.StepTowards</c> directly, or an intermediate avoidance pass first.</summary>
    public Vector2 WorldDir { get; init; }

    /// <summary>Where the follower stands this tick.</summary>
    public PathFollowState State { get; init; }

    /// <summary>The waypoint this tick is working toward: the point <see cref="WorldDir"/> steers at while
    /// <see cref="State"/> is <see cref="PathFollowState.Following"/>, or the hop landing (paired with
    /// <see cref="HopStart"/>) while <see cref="State"/> is <see cref="PathFollowState.Hopping"/>. Zero when
    /// there is no stored waypoint to name: <see cref="PathFollowState.Arrived"/>,
    /// <see cref="PathFollowState.Unreachable"/>, and the one-tick raw-goal steer that follows consuming a
    /// <see cref="NavPathStatus.Partial"/> path.</summary>
    public Vector2 ActiveWaypoint { get; init; }

    /// <summary>The takeoff position (world XZ) of the hop in progress while <see cref="State"/> is
    /// <see cref="PathFollowState.Hopping"/>, and <see cref="Vector2.Zero"/> otherwise. Paired with
    /// <see cref="ActiveWaypoint"/> (the landing) it gives the consumer both ends of the lunge. Resolve each
    /// end's Y from the layer's grid via <see cref="NavGrid.SurfaceHeightAt"/>, the follower stores no Y.</summary>
    public Vector2 HopStart { get; init; }
}

/// <summary>
/// Tuning knobs for a <see cref="PathFollower"/>: how close counts as arrival, how far the goal or the
/// agent may drift from the planned route before a replan is due, and how often replans may fire.
/// </summary>
public sealed class PathFollowConfig
{
    /// <summary>Distance (world units) at which a waypoint or the goal is considered reached.</summary>
    public float AcceptRadius { get; init; } = 0.6f;

    /// <summary>How far (world units) the goal may move from the position it was planned against before
    /// a replan is due.</summary>
    public float GoalRetargetTolerance { get; init; } = 1.5f;

    /// <summary>How far (world units) the agent may stray from the corridor segment leading to the
    /// active waypoint before a replan is due.</summary>
    public float CorridorTolerance { get; init; } = 2.5f;

    /// <summary>Minimum time (seconds) between replans, so a persistently unreachable goal or a jittery
    /// corridor breach does not spam the planner every tick.</summary>
    public float ReplanCooldownSeconds { get; init; } = 0.5f;

    /// <summary>Search budget handed to <see cref="IPathPlanner.FindPath"/> on every replan.</summary>
    public PathQueryBudget Budget { get; init; } = PathQueryBudget.Default;

    /// <summary>Default tuning: a 0.6 unit accept radius, 1.5 unit goal-drift and 2.5 unit corridor
    /// tolerances, a 0.5 second replan cooldown, and <see cref="PathQueryBudget.Default"/>.</summary>
    public static PathFollowConfig Default { get; } = new();
}

/// <summary>
/// Per-agent steering state that turns a moving goal into a per-tick world-space direction, replanning
/// through an <see cref="IPathPlanner"/> only when it must: the stored path runs out, the goal drifts too
/// far from where it was planned, or the agent strays too far off the planned corridor. A game brain owns
/// one instance per agent and calls <see cref="Tick"/> every frame. The output feeds
/// <c>CharacterMovement.StepTowards</c>, possibly through a dynamic-avoidance pass first (see
/// <see cref="PathFollowOutput"/>). Not thread-safe: one agent, one thread.
/// </summary>
public sealed class PathFollower
{
    readonly IPathPlanner _planner;
    readonly PathFollowConfig _config;

    NavPath? _path;
    int _index;
    float _cooldown;
    Vector2 _plannedGoalXz;
    Vector2 _planOriginXz;

    /// <summary>Builds a follower over <paramref name="planner"/>, using <paramref name="config"/> or
    /// <see cref="PathFollowConfig.Default"/> when none is given.</summary>
    public PathFollower(IPathPlanner planner, PathFollowConfig? config = null)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _config = config ?? PathFollowConfig.Default;
    }

    /// <summary>
    /// The committed path the follower is currently steering along, for a consumer that wants to draw or
    /// log the corridor an agent is actually following, or <see langword="null"/> when it is following
    /// none. It is <see langword="null"/> before the first <see cref="Tick"/> plans, after
    /// <see cref="Reset"/>, once the goal is reached (<see cref="PathFollowState.Arrived"/>), while the
    /// goal is <see cref="PathFollowState.Unreachable"/> (the planner found no route), and for the single
    /// gap tick after a fully consumed <see cref="NavPathStatus.Partial"/> path, where <see cref="Tick"/>
    /// clears the exhausted path and steers straight at the raw goal (still
    /// <see cref="PathFollowState.Following"/>) until the next replan picks up a fresh route. While a
    /// replan is merely due but still gated by <see cref="PathFollowConfig.ReplanCooldownSeconds"/>, this
    /// stays the previously committed path, so the reader always sees the route the agent is steering on,
    /// never a re-run of the planner. Reading it is allocation-free and never invokes the planner. The
    /// returned <see cref="NavPath"/> is the instance <see cref="IPathPlanner.FindPath"/> produced, and
    /// its <see cref="NavPath.Waypoints"/> is a read-only view that cannot be downcast to mutable storage
    /// (guaranteed by the <see cref="NavPath"/> constructor), so this is a read-only view with no path
    /// back into the follower's state. When non-null it always carries at least one waypoint and
    /// <see cref="ActiveWaypointIndex"/> is a valid index into its <see cref="NavPath.Waypoints"/>.
    /// </summary>
    public NavPath? ActivePath =>
        _path is { Status: not NavPathStatus.Unreachable, Waypoints.Count: > 0 } ? _path : null;

    /// <summary>
    /// Index into <see cref="ActivePath"/>'s <see cref="NavPath.Waypoints"/> of the waypoint
    /// <see cref="Tick"/> is currently steering toward, so <c>Waypoints[ActiveWaypointIndex]</c> onward is
    /// the corridor still ahead of the agent and everything before it is already consumed. It advances as
    /// waypoints are reached and resets to zero on each replan. Zero, and not meaningful, whenever
    /// <see cref="ActivePath"/> is <see langword="null"/>.
    /// </summary>
    public int ActiveWaypointIndex => ActivePath is null ? 0 : _index;

    /// <summary>
    /// Advances the follower by <paramref name="dt"/> seconds toward <paramref name="goal"/> from
    /// <paramref name="position"/>, replanning through the <see cref="IPathPlanner"/> as needed. Position
    /// and goal are world space (Y ignored for all distance and direction math, which works in XZ). In
    /// order, each tick:
    /// <list type="number">
    /// <item>Drains the replan cooldown by <paramref name="dt"/>.</item>
    /// <item>Returns <see cref="PathFollowState.Arrived"/> immediately if already within
    /// <see cref="PathFollowConfig.AcceptRadius"/> of the goal, clearing any stored path.</item>
    /// <item>Decides whether a replan is due: no stored path, the stored path is fully consumed, the goal
    /// drifted past <see cref="PathFollowConfig.GoalRetargetTolerance"/> from where it was planned, or the
    /// agent strayed past <see cref="PathFollowConfig.CorridorTolerance"/> from the corridor segment
    /// leading to the active waypoint.</item>
    /// <item>Replans when due and the cooldown has fully drained, then resets the cooldown.</item>
    /// <item>Returns <see cref="PathFollowState.Unreachable"/> if there is no usable path (none stored,
    /// or the planner reported <see cref="NavPathStatus.Unreachable"/>, or it has no waypoints). The
    /// cooldown from step 4 naturally throttles retries on later ticks.</item>
    /// <item>Advances past every waypoint already within <see cref="PathFollowConfig.AcceptRadius"/>. If
    /// that consumes the whole path: a <see cref="NavPathStatus.Complete"/> path means the goal is
    /// reached (<see cref="PathFollowState.Arrived"/>). A <see cref="NavPathStatus.Partial"/> path clears
    /// itself and steers straight at the raw goal for this one tick, until the next tick's replan (once
    /// the cooldown allows) picks up a fresh route.</item>
    /// <item>Otherwise steers at the new active waypoint. If that waypoint is a
    /// <see cref="NavWaypointKind.Hop"/> landing, ground steering is suspended instead: the tick returns
    /// <see cref="PathFollowState.Hopping"/> with <see cref="PathFollowOutput.WorldDir"/> zero and both hop
    /// endpoints (<see cref="PathFollowOutput.HopStart"/> to <see cref="PathFollowOutput.ActiveWaypoint"/>)
    /// so the consumer drives its own lunge, and re-emits every tick until step 6 advances past the
    /// landing.</item>
    /// </list>
    /// </summary>
    /// <param name="position">Current world position of the agent.</param>
    /// <param name="goal">Current world position of the goal. May move from tick to tick.</param>
    /// <param name="agentRadius">Agent radius passed through to the planner on a replan.</param>
    /// <param name="dt">Timestep in seconds.</param>
    /// <returns>The steering result for this tick.</returns>
    public PathFollowOutput Tick(Vector3 position, Vector3 goal, float agentRadius, float dt)
    {
        Vector2 posXz = new Vector2(position.X, position.Z);
        Vector2 goalXz = new Vector2(goal.X, goal.Z);

        // Step 1: drain the replan cooldown.
        _cooldown = MathF.Max(0f, _cooldown - dt);

        // Step 2: already at the goal.
        if (Vector2.Distance(posXz, goalXz) <= _config.AcceptRadius)
        {
            _path = null;
            _index = 0;
            return new PathFollowOutput { WorldDir = Vector2.Zero, State = PathFollowState.Arrived, ActiveWaypoint = Vector2.Zero, HopStart = Vector2.Zero };
        }

        // Step 3: decide whether a replan is due.
        bool needsPlan = _path is null
            || _index >= _path.Waypoints.Count
            || Vector2.Distance(goalXz, _plannedGoalXz) > _config.GoalRetargetTolerance
            || DistanceToActiveCorridor(posXz) > _config.CorridorTolerance;

        // Step 4: replan, gated by the cooldown.
        if (needsPlan && _cooldown == 0f)
        {
            _path = _planner.FindPath(position, goal, agentRadius, _config.Budget);
            _plannedGoalXz = goalXz;
            _planOriginXz = posXz;
            _index = 0;
            _cooldown = _config.ReplanCooldownSeconds;
        }

        // Step 5: no usable path yet (or ever).
        if (_path is null || _path.Status == NavPathStatus.Unreachable || _path.Waypoints.Count == 0)
        {
            return new PathFollowOutput { WorldDir = Vector2.Zero, State = PathFollowState.Unreachable, ActiveWaypoint = Vector2.Zero, HopStart = Vector2.Zero };
        }

        // Step 6: advance past every waypoint already reached.
        IReadOnlyList<NavWaypoint> waypoints = _path.Waypoints;
        while (_index < waypoints.Count && Vector2.Distance(posXz, waypoints[_index].Position) <= _config.AcceptRadius)
        {
            _index++;
        }

        if (_index >= waypoints.Count)
        {
            NavPathStatus status = _path.Status;
            _path = null;
            _index = 0;

            if (status == NavPathStatus.Complete)
            {
                return new PathFollowOutput { WorldDir = Vector2.Zero, State = PathFollowState.Arrived, ActiveWaypoint = Vector2.Zero, HopStart = Vector2.Zero };
            }

            // Partial: steer straight at the raw goal for this tick. The next tick's step 3 sees no
            // stored path and replans once the cooldown allows.
            Vector2 towardGoal = Vector2.Normalize(goalXz - posXz);
            return new PathFollowOutput { WorldDir = towardGoal, State = PathFollowState.Following, ActiveWaypoint = Vector2.Zero, HopStart = Vector2.Zero };
        }

        // Step 7: steer at the active waypoint. A Hop landing suspends ground steering: the follower
        // reports Hopping and hands the consumer both ends of the lunge (HopStart to ActiveWaypoint), which
        // drives its own motion until the agent reaches the landing and step 6 advances past it.
        NavWaypoint active = waypoints[_index];
        if (active.Kind == NavWaypointKind.Hop)
        {
            Vector2 hopStart = _index == 0 ? _planOriginXz : waypoints[_index - 1].Position;
            return new PathFollowOutput { WorldDir = Vector2.Zero, State = PathFollowState.Hopping, ActiveWaypoint = active.Position, HopStart = hopStart };
        }

        Vector2 dir = Vector2.Normalize(active.Position - posXz);
        return new PathFollowOutput { WorldDir = dir, State = PathFollowState.Following, ActiveWaypoint = active.Position, HopStart = Vector2.Zero };
    }

    /// <summary>Clears all stored path state (path, index, cooldown, plan origin and goal), as if this
    /// follower had just been constructed. The next <see cref="Tick"/> plans fresh with no cooldown
    /// wait.</summary>
    public void Reset()
    {
        _path = null;
        _index = 0;
        _cooldown = 0f;
        _plannedGoalXz = Vector2.Zero;
        _planOriginXz = Vector2.Zero;
    }

    /// <summary>Point-to-segment distance from <paramref name="posXz"/> to the corridor leading to the
    /// active waypoint: from the previous waypoint (or <see cref="_planOriginXz"/> when the active
    /// waypoint is index 0) to the active waypoint itself. Only called when <see cref="_path"/> is known
    /// non-null with at least one unconsumed waypoint (short-circuited by the earlier checks in
    /// <see cref="Tick"/>).</summary>
    float DistanceToActiveCorridor(Vector2 posXz)
    {
        IReadOnlyList<NavWaypoint> waypoints = _path!.Waypoints;
        Vector2 active = waypoints[_index].Position;
        Vector2 previous = _index == 0 ? _planOriginXz : waypoints[_index - 1].Position;
        return DistancePointToSegment(posXz, previous, active);
    }

    static float DistancePointToSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lengthSquared = ab.LengthSquared();
        if (lengthSquared <= 0f)
        {
            return Vector2.Distance(point, a);
        }

        float t = Math.Clamp(Vector2.Dot(point - a, ab) / lengthSquared, 0f, 1f);
        Vector2 closest = a + ab * t;
        return Vector2.Distance(point, closest);
    }
}
