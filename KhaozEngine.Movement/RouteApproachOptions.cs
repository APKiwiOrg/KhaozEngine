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
}
