using System;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;
using static KhaozEngine.Tests.Navigation.GridPathPlannerDisconnectedProgressTests;
using static KhaozEngine.Tests.Navigation.GridSearchRequestFixtures;

namespace KhaozEngine.Tests.Navigation;

public sealed partial class PathFollowerPlanningTests
{
    [Fact]
    public void ArrivalByMembershipWinsOnTheAdoptionCall()
    {
        var planner = Corridor(3);
        using var pool = Incremental(planner).CreateWorkspaces(1, Budget(2));
        var follower = Follower(planner, pool, 6);
        var destination = End(3);
        for (int call = 1; call <= 23; call++)
            Waiting(follower, follower.Tick(Start, destination, 0f, 0f));

        var current = new NavGoalRegion(Start, 0f, feet => feet == Start);
        var arrived = follower.Tick(Start, current, 0f, 0f);
        Assert.Equal(PathFollowState.Arrived, arrived.State);
        Bits(Vector2.Zero, arrived.WorldDir);
        Assert.Null(follower.ActivePath);
        Assert.False(follower.PlanningPending);
    }

    [Theory]
    [InlineData(true, 15)]
    [InlineData(false, 2)]
    public void DelayedCompleteOrRefusedResultRetriesFromAChangedStartCell(bool complete, int finalSlice)
    {
        var planner = Corridor(3);
        using var pool = Incremental(planner).CreateWorkspaces(1, Budget(2));
        var follower = Follower(planner, pool, 6);
        var destination = complete ? End(2) : new NavGoalRegion(End(3).Anchor, 0f, _ => false);
        // The two-node Complete spends one expansion's neighbours. A refused region stops after
        // snap, member box and two rejected candidates. Neither route has another slice at adoption.
        for (int call = 1; call <= finalSlice; call++)
            Waiting(follower, follower.Tick(Start, destination, 0f, 0f));

        Vector3 pushed = new(2.5f, 0f, 0.5f);
        Waiting(follower, follower.Tick(pushed, destination, 0f, 0f));
        using var waiter = Incremental(planner).BeginPath(Start, destination, 0f, Budget(2), pool);
        Assert.Equal(1, pool.Waits);
        follower.Reset();
    }

    [Fact]
    public void FreshSlicedCompleteAtTheStartCellKeepsTheCooldownOutsideMembership()
    {
        var planner = Corridor(3);
        using var pool = Incremental(planner).CreateWorkspaces(1, Budget(2));
        var follower = new PathFollower(planner, new PathFollowConfig
        {
            Budget = Budget(2),
            Planning = new(new(int.MaxValue), pool),
            ReplanCooldownSeconds = 0.5f,
        });
        Vector3 feet = new(0.5f, 0.001f, 0.5f);
        var goal = End(1);

        var first = follower.Tick(feet, goal, 0f, 0f);
        Assert.Equal(PathFollowState.Following, first.State);
        Bits(Vector2.Zero, first.WorldDir);
        var adopted = Assert.IsType<NavPath>(follower.ActivePath);
        Assert.Equal(NavPathStatus.Complete, adopted.Status);
        Bits(new(0.5f, 0.5f), Assert.Single(adopted.Waypoints).Position);

        var waitingForCooldown = follower.Tick(feet, goal, 0f, 0.25f);
        Assert.Equal(PathFollowState.Following, waitingForCooldown.State);
        Bits(Vector2.Zero, waitingForCooldown.WorldDir);
        Assert.Same(adopted, follower.ActivePath);
        Assert.False(follower.PlanningPending);

        Assert.Equal(PathFollowState.Following, follower.Tick(feet, goal, 0f, 0.25f).State);
        Assert.NotSame(adopted, follower.ActivePath);
    }

    [Fact]
    public void DisposingThePoolClearsLeasedAndQueuedFollowers()
    {
        var planner = Corridor(3);
        using var pool = Incremental(planner).CreateWorkspaces(1, Budget(2));
        var leased = Follower(planner, pool, 6);
        var queued = Follower(planner, pool, 6);
        var goal = End(3);
        Waiting(leased, leased.Tick(Start, goal, 0f, 0f));
        Waiting(queued, queued.Tick(Start, goal, 0f, 0f));
        Assert.Equal(1, pool.Waits);

        pool.Dispose();
        Assert.False(leased.PlanningPending);
        Assert.False(queued.PlanningPending);
        Assert.Throws<ObjectDisposedException>(() => leased.Tick(Start, goal, 0f, 0f));
        leased.Reset();
        queued.Reset();
        Assert.False(leased.PlanningPending);
        Assert.False(queued.PlanningPending);
        Assert.Throws<ObjectDisposedException>(() => queued.Tick(Start, goal, 0f, 0f));
    }

    [Fact]
    public void PendingRegionTickReturnsTheWholeHoldOutput()
    {
        var planner = Corridor(3);
        using var pool = Incremental(planner).CreateWorkspaces(1, Budget(2));
        var follower = Follower(planner, pool, 6);

        var hold = follower.Tick(Start, End(3), 0f, 0f);
        Waiting(follower, hold);
        Bits(Vector2.Zero, hold.HopStart);
        Assert.Null(hold.DropTraversal);
        follower.Reset();
    }
}
