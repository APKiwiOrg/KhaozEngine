using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;
using static KhaozEngine.Movement.RangeApproachCore;

namespace KhaozEngine.Movement;

/// <summary>Per-body ground steering to observed exact shape range. Call Reset for target replacement,
/// teleport or manual cancellation. Target translation uses the follower's drift and cooldown policy.</summary>
public sealed partial class MoveToRange
{
    private readonly GroundNavigation? _navigation;
    private readonly PathFollower _follower;
    private readonly RouteStraightener? _straightener;
    private readonly Func<Vector3, Vector3, bool> _allowsSegment;
    private readonly StepAdmission _admits;
    private readonly RouteApproachOptions _options;

    public MoveToRange(GroundNavigation navigation, PathFollowConfig? follow = null)
        : this(navigation, follow, RouteApproachOptions.Default)
    {
    }

    /// <summary>Route following with opt-in <paramref name="options"/>.</summary>
    /// <exception cref="ArgumentException"><see cref="RouteApproachOptions.SteerWhileSwimming"/> is set and the
    /// profile is not <see cref="GroundNavigation.Aquatic"/>.</exception>
    public MoveToRange(GroundNavigation navigation, PathFollowConfig? follow, RouteApproachOptions options)
        : this((navigation ?? throw new ArgumentNullException(nameof(navigation))).Planner,
            navigation.Space, navigation.AllowsSegment, follow, options)
    {
        if (options.SteerWhileSwimming && !navigation.Aquatic)
            throw new ArgumentException("Steering swimmers needs an aquatic profile.", nameof(options));
        _navigation = navigation;
    }

    /// <summary>The caller supplies equivalent guarded region planning and segment admission.</summary>
    public MoveToRange(IRegionPathPlanner planner, NavSpace space,
        Func<Vector3, Vector3, bool> allowsSegment, PathFollowConfig? follow = null)
        : this(planner, space, allowsSegment, follow, RouteApproachOptions.Default)
    {
    }

    /// <summary>The caller supplies equivalent guarded region planning and segment admission, with opt-in
    /// <paramref name="options"/>.</summary>
    public MoveToRange(IRegionPathPlanner planner, NavSpace space,
        Func<Vector3, Vector3, bool> allowsSegment, PathFollowConfig? follow, RouteApproachOptions options)
    {
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(space);
        _allowsSegment = allowsSegment ?? throw new ArgumentNullException(nameof(allowsSegment));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.StraightenRoutes) _straightener = new RouteStraightener(planner, space, allowsSegment);
        _follower = new PathFollower(_straightener ?? planner,
            StrictConfig(follow ?? PathFollowConfig.Default, options.CarryThroughStraightRuns), space);
        _contains = Contains;
        _admits = AllowsStep;
    }

    /// <summary>Returns bounded requested input without writing the supplied state or stepping the world.
    /// Input validation and current reach evaluation run before status selection. Suspended takes precedence over
    /// InRange for airborne or committed bodies, and for a settling swimmer under
    /// <see cref="RouteApproachOptions.SteerWhileSwimming"/>. InRange witnesses the current supplied capsule, never a
    /// waypoint or a predicted destination. Range uses no tolerance.</summary>
    public RangeSteering Tick(in MoveState body, in MoveTuning tuning, in ReachTarget target,
        float range, bool run, float dt, GroundMoveContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.ValidateTuning(tuning);
        _navigation?.ValidateTuning(tuning);
        ValidateBody(body);
        if (!float.IsFinite(dt) || dt <= 0f) throw new ArgumentOutOfRangeException(nameof(dt));
        MovementBody shape = Body(body, tuning);
        bool within = ReachGeometry.Within(shape, target, range);
        bool swims = _options.SteerWhileSwimming && body.Swimming;
        if ((!body.Grounded && !swims) || body.Commitment.IsActive) return Hold(RangeMoveStatus.Suspended);
        if (swims && Settling(body, tuning, context)) return Hold(RangeMoveStatus.Suspended);
        if (within) return Hold(RangeMoveStatus.InRange);

        NavGoalRegion goal = Goal(tuning, target, range);
        Vector3 feet = Feet(body, tuning);
        PathFollowOutput route = _follower.Tick(feet, goal, tuning.CapsuleRadius, dt);
        if (route.State == PathFollowState.Hopping) return Hold(RangeMoveStatus.UnsupportedTransition);
        if (route.State == PathFollowState.WaitingForPath) return Hold(RangeMoveStatus.WaitingForPath);
        if (route.State == PathFollowState.Unreachable) return Hold(RangeMoveStatus.Unreachable);
        if (route.State != PathFollowState.Following) return Hold(RangeMoveStatus.Following);

        float bound = _options.SteerWhileSwimming
            ? SwimPace.Bound(body, tuning, run, dt, context)
            : TravelBound(body, tuning, run, dt, context);
        if (bound == 0f) return Hold(RangeMoveStatus.Following);
        if (_follower.ActivePath?.Status == NavPathStatus.Complete &&
            TryApproach(body, tuning, target, range, run, dt, context, bound, out Vector2 approach))
            return new RangeSteering(approach, RangeMoveStatus.Following);

        if (!TryCarry(body, tuning, run, dt, context, route.ActiveWaypoint, bound, out Vector2 command))
        {
            Vector2 offset = route.ActiveWaypoint - new Vector2(body.Position.X, body.Position.Z);
            command = BoundedDirection(offset, bound);
            if (command == Vector2.Zero) return Hold(RangeMoveStatus.Following);
        }
        MoveState predicted = context.Step(body, command, run, dt, tuning);
        if (!AllowsStep(body, predicted, tuning))
        {
            FallBackToRawRoute();
            return Hold(RangeMoveStatus.Following);
        }
        command = StopAtRange(body, tuning, target, range, run, dt, context, command, predicted, _admits);
        return new RangeSteering(command, RangeMoveStatus.Following);
    }

    /// <summary>Clears the route, shape snapshot and replan cooldown.</summary>
    public void Reset()
    {
        _follower.Reset();
        _goal = null;
    }

    private static RangeSteering Hold(RangeMoveStatus status) => new(Vector2.Zero, status);

    // A swimmer settles until the medium at its feet is water and its feet lie in the band Resolve accepts around its
    // float line, as an airborne body lands before it is steered.
    private static bool Settling(in MoveState body, in MoveTuning tuning, GroundMoveContext context)
    {
        if (context.Medium is not { } medium) return true;
        float feetY = body.Position.Y - tuning.CapsuleHalfHeight;
        MovementMedium sample = medium(body.Position.X, body.Position.Z, feetY);
        if (!sample.InWater) return true;
        float floatLine = sample.WaterSurfaceY - tuning.SwimSurfaceSubmersionFraction * (2f * tuning.CapsuleHalfHeight);
        double band = Math.Max(tuning.StepHeight, GroundTraversalProbe.ArrivalTolerance);
        return !(Math.Abs((double)feetY - floatLine) <= band);
    }

    private static PathFollowConfig StrictConfig(PathFollowConfig follow, bool carry)
    {
        static void Nonnegative(float value)
        {
            if (!float.IsFinite(value) || value < 0f)
                throw new ArgumentOutOfRangeException(nameof(follow), "Follower controls must be finite and nonnegative.");
        }
        Nonnegative(follow.AcceptRadius);
        Nonnegative(follow.GoalRetargetTolerance);
        Nonnegative(follow.CorridorTolerance);
        Nonnegative(follow.ReplanCooldownSeconds);
        if (!float.IsPositiveInfinity(follow.VerticalAcceptTolerance)) Nonnegative(follow.VerticalAcceptTolerance);
        if (!float.IsPositiveInfinity(follow.GoalRetargetVerticalTolerance)) Nonnegative(follow.GoalRetargetVerticalTolerance);
        return new PathFollowConfig
        {
            // Physical cell edges must survive the general follower's broad point tolerance.
            AcceptRadius = MathF.Min(follow.AcceptRadius, 0.00001f),
            ConsumePassedCollinearWaypoints = carry || follow.ConsumePassedCollinearWaypoints,
            VerticalAcceptTolerance = follow.VerticalAcceptTolerance,
            GoalRetargetTolerance = follow.GoalRetargetTolerance,
            GoalRetargetVerticalTolerance = follow.GoalRetargetVerticalTolerance,
            CorridorTolerance = follow.CorridorTolerance,
            ReplanCooldownSeconds = follow.ReplanCooldownSeconds,
            Budget = follow.Budget,
        };
    }
}
