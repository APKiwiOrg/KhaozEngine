using System.Numerics;

namespace KhaozEngine.Movement;

/// <summary>Admission, travel or terminal outcome of a GroundPursuit. Only Ready admits a new commitment.</summary>
public enum GroundPursuitStatus
{
    UnsupportedState,
    Ready,
    AlreadyInRange,
    OutOfRange,
    NoPath,
    TooLong,
    Travelling,
    Arrived,
    TargetLost,
}

/// <summary>Machine-readable diagnostics, independent of developer prose. PartialRoute and UnreachableRoute
/// retain planner outcomes. NonWalkRoute includes Hop, Drop and unknown waypoint kinds. InvalidEndpoint means
/// missing physical heights or an endpoint outside current exact reach. InvalidEntry means a kept segment,
/// including actual-to-route entry or a shortcut, failed supported-walk admission. NoDirection means the route
/// cannot supply a usable horizontal commitment direction. ProfileMismatch includes unsupported context bindings.</summary>
public enum GroundPursuitDetail
{
    None,
    PartialRoute,
    UnreachableRoute,
    NonWalkRoute,
    InvalidEndpoint,
    InvalidEntry,
    EmptyRoute,
    NoDirection,
    ProfileMismatch,
    Airborne,
    Swimming,
    JumpPending,
    ActiveCommitment,
    NotAdmitted,
}

/// <summary>A pursuit decision, with no actor identity, physics mutation or player-facing text.</summary>
/// <param name="Status">Ready begins travel. Travelling steers or holds. Arrived witnesses current exact 3D
/// reach. Other statuses refuse admission or terminate pursuit.</param>
/// <param name="Direction">Returned Ready and Travelling results carry a finite nonzero unit world XZ
/// direction, suitable for ground commitment controls. Other results carry zero.</param>
/// <param name="RemainingPathLength">Horizontal XZ metres through the remaining corridor. Slope rise does not
/// consume budget. Ready is finite and positive. Travelling is finite and nonnegative, with zero holding during
/// replan cooldown. This is neither surface arc length nor an arrival witness.</param>
/// <param name="Detail">Typed diagnostic detail. None when no additional diagnostic applies.</param>
public readonly record struct GroundPursuitResult(GroundPursuitStatus Status, Vector2 Direction,
    float RemainingPathLength, GroundPursuitDetail Detail = GroundPursuitDetail.None);
