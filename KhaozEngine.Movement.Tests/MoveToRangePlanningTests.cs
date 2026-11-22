using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement.Fixtures;
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
            RangeSteering steer = Tick(mover, body, target, context);
            AssertSteering(new(call == 3 ? ArrivalCommand : Vector2.UnitX, RangeMoveStatus.Following), steer);
            AssertRoute(mover, NavPathStatus.Complete, routes[call]);
            body = context.Step(body, steer.WorldDirection, false, Dt, Tuning);
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
            Vector2 command = call == 4 ? ArrivalCommand : call == 5 ? Vector2.Zero : Vector2.UnitX;
            RangeSteering expected = new(command, call == 5 ? RangeMoveStatus.InRange : RangeMoveStatus.Following);
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
        for (int call = 2; call <= RawSlicesAtQuotaSix(); call++)
            HoldPending(raw, At(0.5f), Far, context);
        AssertSteering(new(Vector2.UnitX, RangeMoveStatus.Following), Tick(raw, At(0.5f), Far, context));
        AssertRoute(raw, NavPathStatus.Complete, [1.5f, 2.5f, 3.5f, 4.5f]);
        Assert.Null(raw.Straightener.LastStraightened);
        HoldPending(shortened, At(0.5f), Far, context);
        Assert.Equal(RangeMoveStatus.Following, Tick(shortened, At(0.5f), Far, context).Status);
        AssertRoute(shortened, NavPathStatus.Complete, [4.5f]);
        Assert.Same(shortened.ActivePathForTests, shortened.Straightener!.LastStraightened);

        var end = Goal(new(4.5f, 0f, 1.5f));
        NavPath next = raw.Straightener.FindPath(new(0.5f, 0f, 1.5f), end, Tuning.CapsuleRadius, PathQueryBudget.Default);
        AssertBits(4.5f, Assert.Single(next.Waypoints).Position.X);
    }

    private static int RawSlicesAtQuotaSix()
    {
        int[] expansion = [4, 1, 6, 6, 6, 6, 6, 6, 6, 6];
        int[] trace =
        [
            1, 2, 3, 2, // Profile start footprint, then its layer.
            2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, // Snap, box, twelve candidates.
            1, 4, 1, // Certificate, seed and progress.
            .. expansion, .. expansion, .. expansion, .. expansion,
            4, 1, // The target popped with no further neighbours.
            1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 1, 1, 1, 1, // Reconstruct four waypoints.
        ];
        int slices = 1, remaining = 6;
        foreach (int weight in trace)
        {
            if (weight > remaining)
            {
                slices++;
                remaining = 6;
            }
            remaining -= weight;
        }
        return slices;
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
        using var scene = new FlatArrivalScene();
        MoveTuning tuning = scene.Tuning;
        var space = NavSpace.Single(NavGrid.FromSurfaces(2, 1, 0.5f, -0.25f, -0.25f,
            (_, _) => new NavSurfaceSample(true, 0f, float.PositiveInfinity), tuning.StepHeight, 2f * tuning.CapsuleHalfHeight));
        var planner = new GridPathPlanner(space);
        using var pool = ((IIncrementalRegionPathPlanner)planner).CreateWorkspaces(1, PathQueryBudget.Default);
        var mover = new MoveToRange(planner, space, (_, _) => true, Follow(pool, 32));
        var body = new MoveState { Position = new(0, tuning.CapsuleHalfHeight, 0), Grounded = true };
        var target = ReachTarget.Point(body.Position with { X = 0.4f });
        using var arrival = new ArrivalPreviewDiagnostics();

        // Snap, member box, two candidate cells, label, seed, first pop and its eight
        // edges, terminal pop, then one waypoint. With quota 32 the slices spend 31, 30 and 19.
        for (int call = 1; call <= 3; call++)
        {
            AssertSteering(new(Vector2.Zero, RangeMoveStatus.WaitingForPath), Step(body));
            Assert.True(mover.PlanningPending);
            Assert.Null(mover.ActivePathForTests);
            Assert.Equal(0, arrival.CertifiedTrials);
            Assert.Equal(0, arrival.FullSteps);
        }

        RangeSteering steering = Step(body);
        Assert.Equal(RangeMoveStatus.Following, steering.Status);
        Assert.False(mover.PlanningPending);
        Assert.InRange(arrival.CertifiedTrials, 1, 32);
        Assert.Equal(0, arrival.FullSteps);
        MoveState arrived = scene.Context.Step(body, steering.WorldDirection, false, 0.1f, tuning);
        Assert.Equal(RangeMoveStatus.InRange, Step(arrived).Status);

        RangeSteering Step(MoveState at) => mover.Tick(at, tuning, target, 0.1f, false, 0.1f, scene.Context);
    }

    // At X=2, the smallest command rounding the next position to X=2.5 is the halfway tie.
    static Vector2 ArrivalCommand => new(BitConverter.Int32BitsToSingle(0x3f7ffffc), 0f);

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
