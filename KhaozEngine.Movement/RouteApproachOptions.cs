namespace KhaozEngine.Movement;

/// <summary>Opt-in route behaviour for <see cref="MoveToRange"/>. <see cref="Default"/> keeps every behaviour of the
/// constructors without options.</summary>
public sealed record RouteApproachOptions
{
    /// <summary>Every option off.</summary>
    public static RouteApproachOptions Default { get; } = new();

    /// <summary>Carries travel past waypoints on a straight run of the route, so a body holds full pace instead of
    /// landing on every cell centre. When the active waypoint is a collinear pass-through closer than one tick of
    /// travel, the body aims at the run end, and the follower consumes waypoints it passed on that run. A corner, a
    /// layer change or a hop is never carried past. A carried step the guard refuses falls back to the active
    /// waypoint. Default false.</summary>
    public bool CarryThroughStraightRuns { get; init; }

    /// <summary>Steers a swimming body on an aquatic route. A swimming body is held <see cref="RangeMoveStatus.Suspended"/>
    /// while it settles, until the medium at its feet is water and its feet lie within <c>max(StepHeight, 1 mm)</c> of
    /// its float line. A settled swimmer follows the route at swim pace, so a swim tick never overshoots a waypoint,
    /// and a step is admitted when its prediction swims or stands grounded. Airborne and committed bodies stay
    /// suspended as before. The <see cref="GroundNavigation"/> constructor refuses it for a profile that is not
    /// <see cref="GroundNavigation.Aquatic"/>. <see cref="DirectMoveToRange"/> never steers swimmers. Default false.</summary>
    public bool SteerWhileSwimming { get; init; }

    /// <summary>Straightens each planned route, so a body walks straight across open ground instead of along the
    /// grid's 45 and 90 degree staircase. From the body's feet, the farthest walk waypoint on the same layer whose
    /// centre line and both side lines, offset just under half a cell, pass the segment guard is kept, the waypoints
    /// before it are dropped, and the scan repeats from the kept waypoint. Waypoints are only dropped, never moved, so
    /// every kept bend is a cell centre the body still lands on exactly. The final waypoint, both ends of a hop and
    /// both ends of a layer change are always kept, and the route's status is unchanged. The side lines cost
    /// shortcuts beside walls and fences, where the staircase remains. Straightening reads feet heights from the
    /// space, so it needs a space with surface heights, and a route over heightless cells (a
    /// <c>NavGrid.FromWalkable</c> grid) stays raw. It runs once per plan, at most one guard check of three lines per
    /// raw and kept waypoint. When the guard refuses a step on a straightened segment, the body holds
    /// <see cref="RangeMoveStatus.Following"/> for that tick and the route is replanned once without straightening.
    /// The plan after it straightens again. Default false.</summary>
    public bool StraightenRoutes { get; init; }

    /// <summary>Latches <see cref="RangeMoveStatus.Blocked"/> when a routed body stops making ground. Every tick that
    /// returns <see cref="RangeMoveStatus.Following"/> or <see cref="RangeMoveStatus.WaitingForPath"/> is counted,
    /// except the hold for a zero travel bound, so a refused step, a refused straightened step, a zero offset waypoint
    /// and an exhausted partial route waiting on its cooldown all count. Suspended, InRange, Unreachable and
    /// UnsupportedTransition ticks count toward nothing. When net horizontal feet displacement across the last
    /// <see cref="RouteStallOptions.WindowTicks"/> counted intervals is under <see cref="RouteStallOptions.TravelMetres"/>,
    /// that tick returns Blocked with zero input, and so does every tick after it until InRange,
    /// <see cref="MoveToRange.Reset"/> or a change of target shape, range or capsule. InRange clears the window. A
    /// replan, a straightening fallback or a target translation does not. A body rooted for good never latches, so
    /// ending that hold stays the caller's rule. A body whose travel bound per tick is under <c>TravelMetres /
    /// WindowTicks</c> latches while it walks. With <see cref="StraightenRoutes"/>, a refused straightened step is one
    /// counted tick of zero travel and the raw replan moves, so the fallback alone never latches, while a raw step the
    /// guard keeps refusing latches within the window. Default null, which never returns Blocked.</summary>
    public RouteStallOptions? Stall { get; init; }
}
