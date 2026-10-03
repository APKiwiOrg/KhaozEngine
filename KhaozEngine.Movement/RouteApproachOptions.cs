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
}
