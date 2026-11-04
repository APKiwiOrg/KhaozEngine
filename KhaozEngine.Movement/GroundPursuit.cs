using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;
using static KhaozEngine.Movement.RangeApproachCore;

namespace KhaozEngine.Movement;

/// <summary>One server-side, walk-only ground pursuit. Produces commitment controls without stepping physics
/// or changing actor state. Positions are absolute world metres. Call Reset for target replacement, teleport or
/// cancellation. Stop ticking after a terminal result and let commitment events own ability cleanup.</summary>
public sealed class GroundPursuit
{
    private readonly GroundNavigation? _navigation;
    private readonly RouteStraightener _planner;
    private readonly NavSpace _space;
    private readonly Func<Vector3, Vector3, bool> _allowsSegment;
    private readonly PathFollowConfig _follow;
    private readonly PathQueryBudget _budget;
    private GroundPursuitSettings _settings;
    private GroundPursuitRoute? _route;
    private Vector3 _plannedAnchor;
    private Vector2 _lastDirection;
    private float _cooldown;
    private bool _freshTerminal;

    /// <summary>Uses the profile's guarded planner and supported-walk segment admission. The runtime capsule
    /// and resolved context binding must match the profile. Follower defaults supply query and replan policy.</summary>
    public GroundPursuit(GroundNavigation navigation, PathFollowConfig? follow = null)
        : this((navigation ?? throw new ArgumentNullException(nameof(navigation))).Planner,
            navigation.Space, navigation.AllowsSegment, follow)
    {
        _navigation = navigation;
    }

    /// <summary>The caller supplies equivalent guarded region planning, surface heights and segment admission,
    /// including runtime profile compatibility. Hop, Drop and unknown waypoint kinds are still refused even
    /// if this planner ignores PathQueryBudget.WalkOnly. The supplied follower configuration is immutable.</summary>
    public GroundPursuit(IRegionPathPlanner planner, NavSpace space,
        Func<Vector3, Vector3, bool> allowsSegment, PathFollowConfig? follow = null)
    {
        ArgumentNullException.ThrowIfNull(planner);
        _space = space ?? throw new ArgumentNullException(nameof(space));
        _allowsSegment = allowsSegment ?? throw new ArgumentNullException(nameof(allowsSegment));
        _follow = follow ?? PathFollowConfig.Default;
        ValidateFollow(_follow);
        _budget = _follow.Budget with { WalkOnly = true };
        _planner = new RouteStraightener(planner, space, allowsSegment);
    }

    /// <summary>Starts a fresh pursuit. Current exact 3D reach precedes the shape-distance trigger check.
    /// A Ready result always has finite nonzero direction and positive finite horizontal remaining length,
    /// suitable for a ground commitment request. Path length is XZ metres, so slope rise does not consume budget.
    /// Malformed parameters throw BCL exceptions. Operational refusals carry Status and Detail values.</summary>
    public GroundPursuitResult Admit(in MoveState body, in MoveTuning tuning, in ReachTarget target,
        in GroundPursuitSettings settings, GroundMoveContext context)
    {
        settings.Validate();
        ValidateBody(body);
        Reset();
        GroundPursuitDetail unsupported = Unsupported(body, tuning, context, admitting: true);
        if (unsupported != GroundPursuitDetail.None)
            return Finish(GroundPursuitStatus.UnsupportedState, unsupported);
        MovementBody shape = Body(body, tuning);
        if (ReachGeometry.Within(shape, target, settings.StopDistance))
            return Finish(GroundPursuitStatus.AlreadyInRange);
        if (!ReachGeometry.Within(shape, target, settings.TriggerRange))
            return Finish(GroundPursuitStatus.OutOfRange);
        _settings = settings;
        var goal = new GroundPursuitGoal(tuning, target, settings.StopDistance);
        return Plan(Feet(body, tuning), goal.Region, tuning.CapsuleRadius, GroundPursuitStatus.Ready);
    }

    /// <summary>Rechecks the current target's exact 3D reach before following or replanning. Trigger range
    /// applies only at admission. Every replan applies the path cap to the current horizontal XZ remaining route,
    /// not surface arc length or cumulative travel. The commitment timeout bounds total duration.
    /// Travelling always carries a finite nonzero direction. While a due replan cools down it carries the last
    /// valid direction and zero remaining length, which holds the commitment without claiming arrival.
    /// A valid final approach keeps steering inside AcceptRadius. Terminal proximity only changes the timing
    /// of an otherwise due refresh. No usable horizontal direction outside exact reach also makes a refresh due.
    /// A formerly nonterminal corridor reaching a stale terminal refreshes once immediately under radius and
    /// layer checks. A fresh query already terminal keeps the cooldown.</summary>
    public GroundPursuitResult Tick(in MoveState body, in MoveTuning tuning, ReachTarget? target,
        float dt, GroundMoveContext context)
    {
        if (!float.IsFinite(dt) || dt <= 0) throw new ArgumentOutOfRangeException(nameof(dt));
        ValidateBody(body);
        GroundPursuitDetail unsupported = Unsupported(body, tuning, context, admitting: false);
        if (unsupported != GroundPursuitDetail.None)
            return Finish(GroundPursuitStatus.UnsupportedState, unsupported);
        if (_route is null) return Finish(GroundPursuitStatus.UnsupportedState, GroundPursuitDetail.NotAdmitted);
        if (target is not { } current) return Finish(GroundPursuitStatus.TargetLost);
        if (ReachGeometry.Within(Body(body, tuning), current, _settings.StopDistance))
            return Finish(GroundPursuitStatus.Arrived);
        var goal = new GroundPursuitGoal(tuning, current, _settings.StopDistance);
        Vector3 feet = Feet(body, tuning);
        _cooldown = MathF.Max(0, _cooldown - dt);
        bool entryAllowed = _route.Advance(feet, _follow.AcceptRadius, _allowsSegment);
        bool terminal = _route.AtTerminal(feet, _follow.AcceptRadius);
        if (!terminal) _freshTerminal = false;
        // Proximity changes refresh timing only. A valid final approach must keep its travel budget.
        // Exact horizontal exhaustion still needs a query if current 3D reach has not witnessed arrival.
        bool due = !entryAllowed || !_route.TryDirection(feet, out _) || !goal.Region.Contains(_route.Endpoint)
            || GroundPursuitRoute.Distance(goal.Region.Anchor, _plannedAnchor) > _follow.GoalRetargetTolerance
            || Math.Abs((double)goal.Region.Anchor.Y - _plannedAnchor.Y) > _follow.GoalRetargetVerticalTolerance
            || _route.CorridorDistance(feet) > _follow.CorridorTolerance
            || !_allowsSegment(feet, _route.Active);
        if (due)
        {
            if (_cooldown > 0 && !(terminal && !_freshTerminal))
                return new(GroundPursuitStatus.Travelling, _lastDirection, 0);
            return Plan(feet, goal.Region, tuning.CapsuleRadius, GroundPursuitStatus.Travelling);
        }
        return Steering(feet, GroundPursuitStatus.Travelling);
    }

    /// <summary>Discards admission, route, direction and cooldown. Reset before changing target identity,
    /// teleporting or manually cancelling. Tick after reset returns UnsupportedState with NotAdmitted.</summary>
    public void Reset()
    {
        _route = null;
        _settings = default;
        _plannedAnchor = default;
        _lastDirection = default;
        _cooldown = 0;
        _freshTerminal = false;
    }

    private GroundPursuitResult Plan(Vector3 feet, NavGoalRegion goal, float radius, GroundPursuitStatus status)
    {
        NavPath path = _planner.FindPath(feet, goal, radius, _budget);
        GroundPursuitDetail detail = GroundPursuitRoute.Validate(path, _space, goal, feet, _allowsSegment, out var route);
        if (detail != GroundPursuitDetail.None) return Finish(GroundPursuitStatus.NoPath, detail);
        _route = route!;
        _route.Advance(feet, _follow.AcceptRadius, _allowsSegment);
        _plannedAnchor = goal.Anchor;
        _cooldown = _follow.ReplanCooldownSeconds;
        _freshTerminal = _route.AtTerminal(feet, _follow.AcceptRadius);
        double length = _route.Remaining(feet);
        if (length > _settings.MaximumPathLength) return Finish(GroundPursuitStatus.TooLong);
        return Steering(feet, status);
    }

    private GroundPursuitResult Steering(Vector3 feet, GroundPursuitStatus status)
    {
        double length = _route!.Remaining(feet);
        if (length <= 0 || length > float.MaxValue || !_route.TryDirection(feet, out Vector2 direction))
            return Finish(GroundPursuitStatus.NoPath, GroundPursuitDetail.NoDirection);
        _lastDirection = direction;
        return new(status, direction, (float)length);
    }

    private GroundPursuitDetail Unsupported(in MoveState body, in MoveTuning tuning, GroundMoveContext context, bool admitting)
    {
        ArgumentNullException.ThrowIfNull(context);
        try { context.ValidateTuning(tuning); }
        catch (NotSupportedException) { return GroundPursuitDetail.ProfileMismatch; }
        if (_navigation is not null)
        {
            try { _navigation.Validate(context.ResolvedBinding); }
            catch (ArgumentException) { return GroundPursuitDetail.ProfileMismatch; }
            catch (NotSupportedException) { return GroundPursuitDetail.ProfileMismatch; }
        }
        if (!body.Grounded) return GroundPursuitDetail.Airborne;
        if (body.Swimming || body.WaterExcursion != WaterExcursionState.None) return GroundPursuitDetail.Swimming;
        if (body.JumpBufferRemaining > 0) return GroundPursuitDetail.JumpPending;
        if (body.Commitment.IsActive && (admitting || body.Commitment.Kind != MovementCommitmentKind.Ground ||
            body.Commitment.Phase is not (MovementCommitmentPhase.Preparing or MovementCommitmentPhase.Travelling)))
            return GroundPursuitDetail.ActiveCommitment;
        return GroundPursuitDetail.None;
    }

    private GroundPursuitResult Finish(GroundPursuitStatus status, GroundPursuitDetail detail = GroundPursuitDetail.None)
    {
        Reset();
        return new(status, default, 0, detail);
    }

    private static void ValidateFollow(PathFollowConfig follow)
    {
        static void Nonnegative(float value)
        {
            if (!float.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(follow));
        }
        Nonnegative(follow.AcceptRadius);
        Nonnegative(follow.GoalRetargetTolerance);
        Nonnegative(follow.CorridorTolerance);
        Nonnegative(follow.ReplanCooldownSeconds);
        if (!float.IsPositiveInfinity(follow.GoalRetargetVerticalTolerance)) Nonnegative(follow.GoalRetargetVerticalTolerance);
        Nonnegative(follow.Budget.SnapRadius);
        if (follow.Budget.MaxExpandedNodes < 0) throw new ArgumentOutOfRangeException(nameof(follow));
    }
}
