using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

/// <summary>Per-body ground steering to observed exact shape range. Call Reset for target replacement,
/// teleport or manual cancellation. Target translation uses the follower's drift and cooldown policy.</summary>
public sealed partial class MoveToRange
{
    private readonly GroundNavigation? _navigation;
    private readonly PathFollower _follower;
    private readonly Func<Vector3, Vector3, bool> _allowsSegment;

    public MoveToRange(GroundNavigation navigation, PathFollowConfig? follow = null)
        : this((navigation ?? throw new ArgumentNullException(nameof(navigation))).Planner,
            navigation.Space, navigation.AllowsSegment, follow)
        => _navigation = navigation;

    /// <summary>The caller supplies equivalent guarded region planning and segment admission.</summary>
    public MoveToRange(IRegionPathPlanner planner, NavSpace space,
        Func<Vector3, Vector3, bool> allowsSegment, PathFollowConfig? follow = null)
    {
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(space);
        _allowsSegment = allowsSegment ?? throw new ArgumentNullException(nameof(allowsSegment));
        _follower = new PathFollower(planner, StrictConfig(follow ?? PathFollowConfig.Default), space);
        _contains = Contains;
    }

    /// <summary>Returns bounded requested input without writing the supplied state or stepping the world.
    /// Input validation and current reach evaluation run before status selection. Suspended takes precedence over
    /// InRange for airborne or committed bodies. InRange witnesses the current supplied capsule, never a waypoint
    /// or a predicted destination. Range uses no tolerance.</summary>
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
        if (!body.Grounded || body.Commitment.IsActive) return Hold(RangeMoveStatus.Suspended);
        if (within) return Hold(RangeMoveStatus.InRange);

        NavGoalRegion goal = Goal(tuning, target, range);
        Vector3 feet = Feet(body, tuning);
        PathFollowOutput route = _follower.Tick(feet, goal, tuning.CapsuleRadius, dt);
        if (route.State == PathFollowState.Hopping) return Hold(RangeMoveStatus.UnsupportedTransition);
        if (route.State == PathFollowState.WaitingForPath) return Hold(RangeMoveStatus.WaitingForPath);
        if (route.State == PathFollowState.Unreachable) return Hold(RangeMoveStatus.Unreachable);
        if (route.State != PathFollowState.Following) return Hold(RangeMoveStatus.Following);

        float bound = TravelBound(body, tuning, run, dt, context);
        if (bound == 0f) return Hold(RangeMoveStatus.Following);
        if (_follower.ActivePath?.Status == NavPathStatus.Complete &&
            TryApproach(body, tuning, target, range, run, dt, context, bound, out Vector2 approach))
            return new RangeSteering(approach, RangeMoveStatus.Following);

        Vector2 offset = route.ActiveWaypoint - new Vector2(body.Position.X, body.Position.Z);
        Vector2 command = BoundedDirection(offset, bound);
        if (command == Vector2.Zero) return Hold(RangeMoveStatus.Following);
        MoveState predicted = context.Step(body, command, run, dt, tuning);
        if (!AllowsStep(body, predicted, tuning)) return Hold(RangeMoveStatus.Following);
        command = StopAtRange(body, tuning, target, range, run, dt, context, command, predicted);
        return new RangeSteering(command, RangeMoveStatus.Following);
    }

    /// <summary>Clears the route, shape snapshot and replan cooldown.</summary>
    public void Reset()
    {
        _follower.Reset();
        _goal = null;
    }

    private static RangeSteering Hold(RangeMoveStatus status) => new(Vector2.Zero, status);
    private static MovementBody Body(in MoveState body, in MoveTuning tuning)
        => new(body.Position, tuning.CapsuleRadius, tuning.CapsuleHalfHeight);
    private static Vector3 Feet(in MoveState body, in MoveTuning tuning)
        => body.Position - new Vector3(0f, tuning.CapsuleHalfHeight, 0f);

    private static void ValidateBody(in MoveState body)
    {
        if (!MovementBody.IsFinite(body.Position) || !float.IsFinite(body.SpeedScale) ||
            !float.IsFinite(body.VerticalVelocity) || !float.IsFinite(body.TimeSinceGrounded) ||
            !float.IsFinite(body.JumpBufferRemaining) || !float.IsFinite(body.FacingYaw) ||
            !float.IsFinite(body.HorizontalVelocity.X) || !float.IsFinite(body.HorizontalVelocity.Y) ||
            !float.IsFinite(body.ClimbRateEwma))
            throw new ArgumentOutOfRangeException(nameof(body), "Movement state must be finite.");
    }

    private static PathFollowConfig StrictConfig(PathFollowConfig follow)
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
            VerticalAcceptTolerance = follow.VerticalAcceptTolerance,
            GoalRetargetTolerance = follow.GoalRetargetTolerance,
            GoalRetargetVerticalTolerance = follow.GoalRetargetVerticalTolerance,
            CorridorTolerance = follow.CorridorTolerance,
            ReplanCooldownSeconds = follow.ReplanCooldownSeconds,
            Budget = follow.Budget,
        };
    }
}
