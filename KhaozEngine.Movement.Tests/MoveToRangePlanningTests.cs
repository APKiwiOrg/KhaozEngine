using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using Xunit;
using static KhaozEngine.Tests.Movement.SegmentAdmissionFixture;

namespace KhaozEngine.Tests.Movement;

public sealed partial class MoveToRangePlanningTests
{
    static readonly MoveTuning Tuning = MoveTuning.Default with { CapsuleRadius = 0.25f, WalkSpeed = 2f };
    static readonly RouteApproachOptions Straight = new() { StraightenRoutes = true };
    static readonly RouteApproachOptions Stall = new() { Stall = new(15, 0.1f) };
    static readonly ReachTarget Far = Target(4.5f);
    const float Dt = 0.25f;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultOptionsUnchanged(bool straighten)
    {
        var navigation = Flat(6, 3, Tuning.CapsuleRadius);
        var mover = new MoveToRange(navigation, null, straighten ? Straight : RouteApproachOptions.Default);
        using var context = new GroundMoveContext((_, _) => 0f);
        MoveState body = At(0.5f);
        ReachTarget target = Target(2.75f);
        float[][] routes = straighten ? [[2.5f], [2.5f], [2.5f], [2.5f]] :
            [[1.5f, 2.5f], [1.5f, 2.5f], [1.5f, 2.5f], [1.5f, 2.5f]];
        for (int call = 0; call < routes.Length; call++)
        {
            AssertBits(0.5f + 0.5f * call, body.Position.X);
            AssertSteering(new(Vector2.UnitX, RangeMoveStatus.Following), Tick(mover, body, target, context));
            AssertRoute(mover, NavPathStatus.Complete, routes[call]);
            body = context.Step(body, Vector2.UnitX, false, Dt, Tuning);
        }
        AssertBits(2.5f, body.Position.X);
        AssertSteering(new(Vector2.Zero, RangeMoveStatus.InRange), Tick(mover, body, target, context));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletesInFirstCallLikeSynchronous(bool straighten)
    {
        var navigation = Flat(6, 3, Tuning.CapsuleRadius);
        using var pool = navigation.CreatePlanningWorkspaces(1);
        using var context = new GroundMoveContext((_, _) => 0f);
        var options = Stall with { StraightenRoutes = straighten };
        var synchronous = new MoveToRange(navigation, null, options);
        var planned = new MoveToRange(navigation, Follow(pool, int.MaxValue), options);
        ReachTarget target = Target(2.75f);
        MoveState body = At(0.5f);
        for (int call = 1; call <= 5; call++)
        {
            RangeSteering expected = new(call == 5 ? Vector2.Zero : Vector2.UnitX,
                call == 5 ? RangeMoveStatus.InRange : RangeMoveStatus.Following);
            AssertSteering(expected, Tick(synchronous, body, target, context));
            AssertSteering(expected, Tick(planned, body, target, context));
            Assert.False(planned.PlanningPending);
            AssertRoute(synchronous, NavPathStatus.Complete, straighten ? [2.5f] : [1.5f, 2.5f]);
            AssertRoute(planned, NavPathStatus.Complete, straighten ? [2.5f] : [1.5f, 2.5f]);
            Assert.Equal(synchronous.ActiveWaypointIndexForTests, planned.ActiveWaypointIndexForTests);
            if (call < 5) body = context.Step(body, expected.WorldDirection, false, Dt, Tuning);
        }
    }

    [Fact]
    public void TwoStraightenedMoversShareOneWorkspaceAndKeepTheirOwnFallback()
    {
        var navigation = Flat(6, 3, Tuning.CapsuleRadius);
        using var pool = navigation.CreatePlanningWorkspaces(1);
        using var context = new GroundMoveContext((_, _) => 0f);
        var raw = new MoveToRange(navigation, Follow(pool, 6), Straight);
        var shortened = new MoveToRange(navigation, Follow(pool, int.MaxValue), Straight);
        raw.Straightener!.FallBackOnce();

        HoldPending(raw, At(0.5f), Far, context);
        HoldPending(shortened, At(0.5f), Far, context);
        Assert.Equal(1, pool.Waits);
        // A prewarmed raw 5x3-strip query of this extent has fewer than 1000 units.
        // The second owner remains waiting even with a large quota until the first releases.
        for (int call = 2; call <= 200 && raw.PlanningPending; call++)
            Tick(raw, At(0.5f), Far, context);
        AssertRoute(raw, NavPathStatus.Complete, [1.5f, 2.5f, 3.5f, 4.5f]);
        Assert.Null(raw.Straightener.LastStraightened);
        HoldPending(shortened, At(0.5f), Far, context);
        Assert.Equal(RangeMoveStatus.Following, Tick(shortened, At(0.5f), Far, context).Status);
        AssertRoute(shortened, NavPathStatus.Complete, [4.5f]);
        Assert.Same(shortened.ActivePathForTests, shortened.Straightener!.LastStraightened);

        raw.Reset();
        for (int call = 1; call <= 400; call++)
        {
            Tick(raw, At(0.5f), Far, context);
            if (!raw.PlanningPending) break;
        }
        AssertRoute(raw, NavPathStatus.Complete, [4.5f]);
    }

    [Fact]
    public void PendingDoesNotLatchBlocked()
    {
        var navigation = Flat(32, 3, Tuning.CapsuleRadius);
        using var context = new GroundMoveContext((_, _) => 0f);
        using var pool = navigation.CreatePlanningWorkspaces(1);
        var mover = new MoveToRange(navigation, Follow(pool, 6), Stall);
        for (int call = 1; call <= 30; call++)
            HoldPending(mover, At(0.5f), Target(29.5f), context);
        mover.Reset();
    }

    [Fact]
    public void ExhaustedPartialWaitStillLatchesBlocked()
    {
        var navigation = Flat(6, 3, Tuning.CapsuleRadius);
        using var context = new GroundMoveContext((_, _) => 0f);
        using var pool = navigation.CreatePlanningWorkspaces(1);
        var budget = PathQueryBudget.Default with { MaxExpandedNodes = 2 };
        var config = new PathFollowConfig { Budget = budget, Planning = new(new(int.MaxValue), pool), ReplanCooldownSeconds = 10f };
        var mover = new MoveToRange(navigation, config, Stall);
        Assert.Equal(RangeMoveStatus.Following, Tick(mover, At(0.5f), Far, context).Status);
        AssertRoute(mover, NavPathStatus.Partial, [1.5f]);
        for (int call = 1; call <= 16; call++)
        {
            RangeMoveStatus status = call == 16 ? RangeMoveStatus.Blocked : RangeMoveStatus.WaitingForPath;
            AssertSteering(new(Vector2.Zero, status), Tick(mover, At(1.5f), Far, context));
            Assert.False(mover.PlanningPending);
        }
    }

    [Fact]
    public void FinalSliceNeverSharesACallWithAStep()
    {
        var navigation = Flat(6, 3, Tuning.CapsuleRadius);
        using var context = new GroundMoveContext((_, _) => 0f);
        using var pool = navigation.CreatePlanningWorkspaces(1);
        var mover = new MoveToRange(navigation, Follow(pool, 128));
        Vector3 start = new(0.5f, 0f, 1.5f);
        IIncrementalRegionPathPlanner planner = (IIncrementalRegionPathPlanner)navigation.Planner;
        var far = Target(4.5f);

        HoldPending(mover, At(start.X), far, context);
        using var witness = planner.BeginPath(start, Goal(start), Tuning.CapsuleRadius, PathQueryBudget.Default, pool);
        Assert.Equal(1, pool.Waits);
        int exitCall = 0;
        for (int call = 2; call <= 8; call++)
        {
            HoldPending(mover, At(start.X), far, context);
            witness.Advance(new(int.MaxValue));
            if (witness.State == PathPlanState.Ready)
            {
                exitCall = call;
                break;
            }
        }
        Assert.InRange(exitCall, 2, 8);
        AssertSteering(new(Vector2.UnitX, RangeMoveStatus.Following), Tick(mover, At(start.X), far, context));
        AssertRoute(mover, NavPathStatus.Complete, [1.5f, 2.5f, 3.5f, 4.5f]);
    }

    static PathFollowConfig Follow(PathPlanWorkspaces pool, int quota) => new() { Planning = new(new(quota), pool) };

    static MoveState At(float x) => new() { Position = new(x, Tuning.CapsuleHalfHeight, 1.5f), Grounded = true, SpeedScale = 1 };

    static ReachTarget Target(float x) => ReachTarget.Point(new(x, Tuning.CapsuleHalfHeight, 1.5f));

    static RangeSteering Tick(MoveToRange mover, MoveState body, ReachTarget target, GroundMoveContext context, float range = 0)
        => mover.Tick(body, Tuning, target, range, false, Dt, context);

    static void HoldPending(MoveToRange mover, MoveState body, ReachTarget target, GroundMoveContext context, float range = 0)
    {
        AssertSteering(new(Vector2.Zero, RangeMoveStatus.WaitingForPath), Tick(mover, body, target, context, range));
        Assert.True(mover.PlanningPending);
        Assert.Null(mover.ActivePathForTests);
    }

    static void AssertRoute(MoveToRange mover, NavPathStatus status, float[] x)
    {
        NavPath path = Assert.IsType<NavPath>(mover.ActivePathForTests);
        Assert.Equal(status, path.Status);
        Assert.Equal(x.Length, path.Waypoints.Count);
        for (int index = 0; index < x.Length; index++)
        {
            NavWaypoint waypoint = path.Waypoints[index];
            AssertBits(x[index], waypoint.Position.X);
            AssertBits(1.5f, waypoint.Position.Y);
            Assert.Equal(NavWaypointKind.Walk, waypoint.Kind);
            Assert.Equal(0, waypoint.Layer);
        }
    }

    static void AssertSteering(RangeSteering expected, RangeSteering actual)
    {
        Assert.Equal(expected.Status, actual.Status);
        AssertBits(expected.WorldDirection.X, actual.WorldDirection.X);
        AssertBits(expected.WorldDirection.Y, actual.WorldDirection.Y);
        Assert.Equal(expected.DropRefusal, actual.DropRefusal);
    }

    static void AssertBits(float expected, float actual)
        => Assert.Equal(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(actual));
}
