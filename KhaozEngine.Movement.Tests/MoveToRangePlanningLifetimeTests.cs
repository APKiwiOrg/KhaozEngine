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
    [Theory]
    [InlineData("shape")]
    [InlineData("range")]
    [InlineData("capsule")]
    [InlineData("reset")]
    [InlineData("in-range")]
    public void ResetCausesReleaseToTheOldestWaiterBeforeStartingAnotherRequest(string cause)
    {
        var navigation = Flat(6, 3, Tuning.CapsuleRadius);
        using var context = new GroundMoveContext((_, _) => 0f);
        using var pool = navigation.CreatePlanningWorkspaces(1);
        var incremental = (IIncrementalRegionPathPlanner)navigation.Planner;
        // Use a caller-owned raw planner for capsule changes. A profile refuses mismatched capsule geometry.
        var grid = new GridPathPlanner(navigation.Space);
        using var rawPool = ((IIncrementalRegionPathPlanner)grid).CreateWorkspaces(1, PathQueryBudget.Default);
        var selected = cause == "capsule" ? rawPool : pool;
        if (cause == "capsule") incremental = grid;
        var mover = cause == "capsule"
            ? new MoveToRange(grid, navigation.Space, (_, _) => true, Follow(selected, 6))
            : new MoveToRange(navigation, Follow(selected, 6));
        MoveState body = At(0.5f);
        HoldPending(mover, body, Far, context);
        var feet = new Vector3(0.5f, 0f, 1.5f);
        using var oldest = incremental.BeginPath(feet, Goal(feet), Tuning.CapsuleRadius, PathQueryBudget.Default, selected);
        Assert.Equal(1, selected.Waits);

        switch (cause)
        {
            case "shape":
                HoldPending(mover, body, ReachTarget.Box(new(4.5f, Tuning.CapsuleHalfHeight, 1.5f), new(0.25f)), context);
                break;
            case "range":
                HoldPending(mover, body, Far, context, 0.25f);
                break;
            case "capsule":
                var wider = Tuning with { CapsuleRadius = 0.3125f };
                Assert.Equal(RangeMoveStatus.WaitingForPath, mover.Tick(body, wider, Far, 0f, false, Dt, context).Status);
                break;
            case "reset":
                mover.Reset();
                Assert.False(mover.PlanningPending);
                break;
            case "in-range":
                AssertSteering(new(Vector2.Zero, RangeMoveStatus.InRange), Tick(mover, body, Target(0.5f), context));
                Assert.False(mover.PlanningPending);
                break;
        }

        oldest.Advance(new(int.MaxValue));
        Assert.Equal(PathPlanState.Ready, oldest.State);
        mover.Reset();
    }

    [Fact]
    public void InRangeDequeuesAWaiterAndSuspensionLeavesItQueued()
    {
        var navigation = Flat(6, 3, Tuning.CapsuleRadius);
        using var context = new GroundMoveContext((_, _) => 0f);
        using var pool = navigation.CreatePlanningWorkspaces(1);
        var planner = (IIncrementalRegionPathPlanner)navigation.Planner;
        Vector3 feet = new(0.5f, 0f, 1.5f);
        using var owner = planner.BeginPath(feet, Goal(feet), Tuning.CapsuleRadius, PathQueryBudget.Default, pool);
        var mover = new MoveToRange(navigation, Follow(pool, int.MaxValue));
        HoldPending(mover, At(0.5f), Far, context);
        using var last = planner.BeginPath(feet, Goal(feet), Tuning.CapsuleRadius, PathQueryBudget.Default, pool);

        AssertSteering(new(Vector2.Zero, RangeMoveStatus.Suspended),
            Tick(mover, At(0.5f) with { Grounded = false }, Target(0.5f), context));
        Assert.True(mover.PlanningPending);
        AssertSteering(new(Vector2.Zero, RangeMoveStatus.InRange), Tick(mover, At(0.5f), Target(0.5f), context));
        Assert.False(mover.PlanningPending);
        owner.Cancel();
        last.Advance(new(int.MaxValue));
        Assert.Equal(PathPlanState.Ready, last.State);
    }

    [Fact]
    public void SuspendedOwnerDoesNotAdvanceAndCompletesOnItsOriginalProfile()
    {
        var navigation = Flat(6, 3, Tuning.CapsuleRadius);
        var replacement = Flat(6, 3, Tuning.CapsuleRadius);
        using var pool = navigation.CreatePlanningWorkspaces(1);
        using var context = new GroundMoveContext((_, _) => 0f);
        var mover = new MoveToRange(navigation, Follow(pool, 6));
        HoldPending(mover, At(0.5f), Far, context);
        Assert.Throws<ArgumentException>(() => new MoveToRange(replacement, Follow(pool, 6)));
        for (int call = 0; call < 100; call++)
            AssertSteering(new(Vector2.Zero, RangeMoveStatus.Suspended),
                Tick(mover, At(0.5f) with { Grounded = false }, Far, context));
        HoldPending(mover, At(0.5f), Far, context);
        for (int call = 0; call < 120 && mover.PlanningPending; call++)
            Tick(mover, At(0.5f), Far, context);
        AssertRoute(mover, NavPathStatus.Complete, [1.5f, 2.5f, 3.5f, 4.5f]);
        Assert.Equal(0, pool.Waits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BudgetMustFitThePoolAtConstruction(bool snap)
    {
        var navigation = Flat(6, 3, Tuning.CapsuleRadius);
        PathQueryBudget limit = new() { MaxExpandedNodes = 4, SnapRadius = 1f };
        using var pool = navigation.CreatePlanningWorkspaces(1, limit);
        PathPlanningConfig planning = new(new(6), pool);
        var fit = new PathFollowConfig { Planning = planning, Budget = limit };
        _ = new MoveToRange(navigation, fit, Straight);
        PathQueryBudget over = snap ? limit with { SnapRadius = 1.25f } : limit with { MaxExpandedNodes = 5 };
        var tooLarge = new PathFollowConfig { Planning = planning, Budget = over };

        Assert.Throws<ArgumentException>(() => new MoveToRange(navigation, tooLarge, Straight));
        Assert.Throws<ArgumentException>(() => new PathFollower(navigation.Planner, tooLarge, navigation.Space));
        Assert.Equal(0, pool.Waits);
    }

    [Fact]
    public void ConstructorsRefuseIncompatibleDoors()
    {
        var navigation = Flat(6, 3, Tuning.CapsuleRadius, (x, _) => x == 1 ? 1u : 0u);
        using var pool = navigation.CreatePlanningWorkspaces(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PathPlanningConfig(new(5), pool));
        Assert.Throws<ArgumentNullException>(() => new PathPlanningConfig(new(6), null!));
        var config = Follow(pool, 6);
        Assert.Throws<ArgumentException>(() => new GroundPursuit(navigation, config));
        Assert.Throws<ArgumentException>(() => new MoveToRange(navigation.Planner, navigation.Space,
            navigation.AllowsSegment, config, Straight));
        var noRequests = new FixedPath(NavPath.Unreachable);
        Assert.Throws<ArgumentException>(() => new PathFollower(noRequests, config));
        Assert.Throws<ArgumentException>(() => new MoveToRange(noRequests, navigation.Space, navigation.AllowsSegment, config));
        Assert.Throws<ArgumentException>(() => new MoveToRange(navigation.ExcludingAreas(1), config, Straight));
        Assert.Throws<ArgumentException>(() => new PathFollower(new GridPathPlanner(navigation.Space), config));

        pool.Dispose();
        Assert.Throws<ObjectDisposedException>(() => new MoveToRange(navigation, config));
    }
}
