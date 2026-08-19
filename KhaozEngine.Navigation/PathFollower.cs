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

    /// <summary>The goal has been reached, either by the arrival shortcut (within
    /// <see cref="PathFollowConfig.AcceptRadius"/> of the goal in XZ AND within
    /// <see cref="PathFollowConfig.VerticalAcceptTolerance"/> of it in Y) or by consuming a
    /// <see cref="NavPathStatus.Complete"/> path to its end. The stored path was cleared.</summary>
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
    /// <summary>Distance (world units) at which a waypoint or the goal is considered reached. Measured in
    /// XZ, so the goal must also pass <see cref="VerticalAcceptTolerance"/> to count as arrival.</summary>
    public float AcceptRadius { get; init; } = 0.6f;

    /// <summary>
    /// Vertical distance (world units) the agent may sit above or below the goal and still count as
    /// arrived. Paired with <see cref="AcceptRadius"/>: the arrival shortcut in
    /// <see cref="PathFollower.Tick"/> needs the XZ distance within <see cref="AcceptRadius"/> AND the Y
    /// difference within this, so a goal on another floor or on a ledge falls through to the planner
    /// instead of reporting arrival on XZ proximity alone.
    /// <para>
    /// The default 0.8 is twice the engine's canonical climbable step (<c>MoveTuning.StepHeight</c>, 0.4),
    /// which also clears the 0.6 of rise a 45-degree max-slope hillside (<c>MoveTuning.MaxSlopeRadians</c>)
    /// gains across the default <see cref="AcceptRadius"/>, so ordinary ground variation beside the goal
    /// still reads as arrival. It sits far below both the 1.8 m shipped character capsule and the 4 m
    /// default dungeon floor pitch (<c>DungeonConfig.FloorHeightMeters</c>), so no floor separation can
    /// pass it, and below a typical hoppable rise, so a ledge goal still routes through the planner.
    /// </para>
    /// Set it to <see cref="float.PositiveInfinity"/> for a purely horizontal arrival check.
    /// </summary>
    public float VerticalAcceptTolerance { get; init; } = 0.8f;

    /// <summary>How far (world units) the goal may move from the position it was planned against before
    /// a replan is due. Measured in XZ, so a goal that changes floor also has to pass
    /// <see cref="GoalRetargetVerticalTolerance"/> to trigger a replan on height alone.</summary>
    public float GoalRetargetTolerance { get; init; } = 1.5f;

    /// <summary>
    /// How far (world units) the goal may move VERTICALLY from the height it was planned against before a
    /// replan is due: the vertical twin of <see cref="GoalRetargetTolerance"/>, checked alongside it rather
    /// than instead of it.
    /// <para>
    /// A goal taking a staircase moves straight up: same XZ, zero horizontal drift, so the horizontal trigger
    /// alone never fires and the follower keeps steering the route it planned to the old floor until some
    /// unrelated trigger (a corridor breach, a consumed path) happens to come along.
    /// </para>
    /// <para>
    /// The default 0.8 matches <see cref="VerticalAcceptTolerance"/>, so any height change big enough that the
    /// arrival check would no longer call the agent and the goal co-located is also big enough to replan for,
    /// while ordinary ground variation under a goal walking level ground is not.
    /// <see cref="ReplanCooldownSeconds"/> still gates how often a due replan actually reaches the planner, so
    /// a goal that bobs vertically cannot spam it.
    /// </para>
    /// Set it to <see cref="float.PositiveInfinity"/> for a purely horizontal retarget trigger.
    /// </summary>
    public float GoalRetargetVerticalTolerance { get; init; } = 0.8f;

    /// <summary>How far (world units) the agent may stray from the corridor segment leading to the
    /// active waypoint before a replan is due.</summary>
    public float CorridorTolerance { get; init; } = 2.5f;

    /// <summary>Minimum time (seconds) between replans, so a persistently unreachable goal or a jittery
    /// corridor breach does not spam the planner every tick.</summary>
    public float ReplanCooldownSeconds { get; init; } = 0.5f;

    /// <summary>Search budget handed to <see cref="IPathPlanner.FindPath"/> on every replan.</summary>
    public PathQueryBudget Budget { get; init; } = PathQueryBudget.Default;

    /// <summary>Default tuning: a 0.6 unit accept radius paired with a 0.8 unit vertical tolerance, 1.5
    /// unit horizontal and 0.8 unit vertical goal-drift tolerances, a 2.5 unit corridor tolerance, a 0.5
    /// second replan cooldown, and <see cref="PathQueryBudget.Default"/>.</summary>
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
    // Optional, and the only thing that lets the follower resolve which layer the AGENT is on. Null keeps the
    // pre-existing XZ-only waypoint advance, so a consumer on a single-layer world (or one that never built a
    // NavSpace of its own) is unaffected.
    readonly NavSpace? _space;

    NavPath? _path;
    int _index;
    float _cooldown;
    Vector2 _plannedGoalXz;
    // The goal's height at plan time, kept beside _plannedGoalXz rather than folded into it, so the two
    // drift triggers keep their own tolerances (a floor apart vertically is a much smaller number than a
    // floor apart horizontally).
    float _plannedGoalY;
    Vector2 _planOriginXz;

    /// <summary>Builds a follower over <paramref name="planner"/>, using <paramref name="config"/> or
    /// <see cref="PathFollowConfig.Default"/> when none is given.
    /// <para><paramref name="space"/> is the <see cref="NavSpace"/> the planner plans over, and is what lets
    /// the waypoint advance in <see cref="Tick"/> compare the agent's own layer
    /// (<see cref="NavSpace.LayerAt"/>) against the layer each <see cref="NavWaypoint"/> carries. Pass it for
    /// any multi-layer world: without it the advance is XZ-only, and the waypoint at the top of a stair link
    /// sits one cell from its lower partner in XZ, well inside <see cref="PathFollowConfig.AcceptRadius"/>, so
    /// the follower consumes it while the agent is still a floor below and skips the climb. Leaving it null
    /// keeps exactly the old behaviour, which is what a single-layer world wants anyway. The space must be able
    /// to RESOLVE a layer from a position: grids with surface heights, or finite Y bands (every engine baker
    /// produces one of those). A multi-layer space of default <c>NavGrid.FromWalkable</c> grids has neither, so
    /// <see cref="NavSpace.LayerAt"/> answers 0 everywhere and the follower never advances past a layer-1
    /// waypoint. And <paramref name="space"/> resolves from the <c>position</c> handed to <see cref="Tick"/>, so
    /// pass the agent's GROUND position, not a capsule centre, or low overhead geometry flips the layer.</para></summary>
    public PathFollower(IPathPlanner planner, PathFollowConfig? config = null, NavSpace? space = null)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _config = config ?? PathFollowConfig.Default;
        _space = space;
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
    /// and goal are world space. Steering and every distance measure work in XZ. Y is read by the arrival
    /// check in step 2 and, when the follower was given a <see cref="NavSpace"/>, to resolve which layer the
    /// agent is standing on for the waypoint advance in step 6. In order, each tick:
    /// <list type="number">
    /// <item>Drains the replan cooldown by <paramref name="dt"/>.</item>
    /// <item>Returns <see cref="PathFollowState.Arrived"/> immediately if already within
    /// <see cref="PathFollowConfig.AcceptRadius"/> of the goal in XZ AND within
    /// <see cref="PathFollowConfig.VerticalAcceptTolerance"/> of it in Y, clearing any stored path. A goal
    /// that clears the XZ radius but not the vertical tolerance (another floor, a ledge) falls through to
    /// the steps below instead, so the layer-aware planner is the one that decides how to reach it.</item>
    /// <item>Decides whether a replan is due: no stored path, the stored path is fully consumed, the goal
    /// drifted past <see cref="PathFollowConfig.GoalRetargetTolerance"/> in XZ or past
    /// <see cref="PathFollowConfig.GoalRetargetVerticalTolerance"/> in Y from where it was planned, or the
    /// agent strayed past <see cref="PathFollowConfig.CorridorTolerance"/> from the corridor segment
    /// leading to the active waypoint.</item>
    /// <item>Replans when due and the cooldown has fully drained, then resets the cooldown.</item>
    /// <item>Returns <see cref="PathFollowState.Unreachable"/> if there is no usable path (none stored,
    /// or the planner reported <see cref="NavPathStatus.Unreachable"/>, or it has no waypoints). The
    /// cooldown from step 4 naturally throttles retries on later ticks.</item>
    /// <item>Advances past every waypoint already within <see cref="PathFollowConfig.AcceptRadius"/> AND, when
    /// the follower was given a <see cref="NavSpace"/>, on the same layer as the agent
    /// (<see cref="NavSpace.LayerAt"/>). A waypoint on another layer is one the agent has to climb to, which
    /// XZ proximity cannot witness, so the follower keeps steering at it until the agent actually gets there.
    /// If that consumes the whole path: a <see cref="NavPathStatus.Complete"/> path means the goal is
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

        // Step 2: already at the goal. Both halves must hold. XZ proximity alone would report Arrived for
        // a goal stacked directly above or below the agent (the floor above, a ledge) and return before
        // the layer-aware planner ever ran, so the agent would never path to it at all.
        if (Vector2.Distance(posXz, goalXz) <= _config.AcceptRadius
            && MathF.Abs(position.Y - goal.Y) <= _config.VerticalAcceptTolerance)
        {
            _path = null;
            _index = 0;
            return new PathFollowOutput { WorldDir = Vector2.Zero, State = PathFollowState.Arrived, ActiveWaypoint = Vector2.Zero, HopStart = Vector2.Zero };
        }

        // Step 3: decide whether a replan is due. The vertical drift is its own term: a goal that takes a
        // staircase moves straight up, so the XZ drift is exactly zero and the horizontal trigger alone
        // would let the follower keep steering a route planned to the floor the goal has left.
        bool needsPlan = _path is null
            || _index >= _path.Waypoints.Count
            || Vector2.Distance(goalXz, _plannedGoalXz) > _config.GoalRetargetTolerance
            || MathF.Abs(goal.Y - _plannedGoalY) > _config.GoalRetargetVerticalTolerance
            || DistanceToActiveCorridor(posXz) > _config.CorridorTolerance;

        // Step 4: replan, gated by the cooldown.
        if (needsPlan && _cooldown == 0f)
        {
            _path = _planner.FindPath(position, goal, agentRadius, _config.Budget);
            _plannedGoalXz = goalXz;
            _plannedGoalY = goal.Y;
            _planOriginXz = posXz;
            _index = 0;
            _cooldown = _config.ReplanCooldownSeconds;
        }

        // Step 5: no usable path yet (or ever).
        if (_path is null || _path.Status == NavPathStatus.Unreachable || _path.Waypoints.Count == 0)
        {
            return new PathFollowOutput { WorldDir = Vector2.Zero, State = PathFollowState.Unreachable, ActiveWaypoint = Vector2.Zero, HopStart = Vector2.Zero };
        }

        // Step 6: advance past every waypoint already reached. XZ proximity alone is not enough: a stair
        // link's upper waypoint sits about one cell from its lower partner in XZ, inside the accept radius,
        // so an agent standing at the bottom is "within reach" of the top and the loop would consume both in
        // one pass and steer at whatever follows, skipping the climb outright. The waypoint already carries
        // the layer it lives on, so when a NavSpace was supplied the agent's own layer has to match. The
        // agent then has to physically get onto that layer before the follower moves on, which is the point:
        // a link the agent cannot actually traverse leaves it steering at the link instead of walking a route
        // it never took, and the consumer's own stuck detection is what should notice that.
        IReadOnlyList<NavWaypoint> waypoints = _path.Waypoints;
        int? agentLayer = _space?.LayerAt(position);
        while (_index < waypoints.Count
            && Vector2.Distance(posXz, waypoints[_index].Position) <= _config.AcceptRadius
            && (agentLayer is null || waypoints[_index].Layer == agentLayer.Value))
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

    /// <summary>Clears all stored path state (path, index, cooldown, plan origin and goal, height
    /// included), as if this follower had just been constructed. The next <see cref="Tick"/> plans fresh
    /// with no cooldown wait.</summary>
    public void Reset()
    {
        _path = null;
        _index = 0;
        _cooldown = 0f;
        _plannedGoalXz = Vector2.Zero;
        _plannedGoalY = 0f;
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
