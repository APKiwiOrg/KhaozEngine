using System;
using System.Collections.Generic;
using System.Threading;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Physics;
using Xunit;
using static KhaozEngine.Tests.Navigation.GridSearchRequestFixtures;

namespace KhaozEngine.Tests.Navigation;

public class PathPlanWorkspacesTests
{
    static PathPlanRequest Begin(IIncrementalRegionPathPlanner planner, PathPlanWorkspaces pool)
        => planner.BeginPath(Start, End(), 0f, PathQueryBudget.Default, pool);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleasedWorkspaceGoesToOldestWaitersNextAdvance(bool waiterCallsFirst)
    {
        var planner = Incremental(Corridor());
        using var pool = planner.CreateWorkspaces(1, PathQueryBudget.Default);
        using var owner = Begin(planner, pool);
        using var oldest = Begin(planner, pool);
        using var youngest = Begin(planner, pool);
        var one = new PathWorkBudget(1);
        if (waiterCallsFirst) oldest.Advance(one);
        Assert.Equal(0, oldest.WorkSpent);
        Finish(owner, int.MaxValue);
        Assert.Equal(0, oldest.WorkSpent);
        youngest.Advance(one);
        Assert.Equal(0, youngest.WorkSpent);
        oldest.Advance(one);
        Assert.True(oldest.WorkSpent > 0);
        Assert.Equal(2, pool.Waits);
    }

    [Fact]
    public void ForeignPoolIsRefusedEvenOnTheSameSpace()
    {
        var space = NavSpace.Single(DisconnectedPlannerFixture.Grid(16, 1, (_, _) => true));
        var first = Incremental(new GridPathPlanner(space));
        var second = Incremental(new GridPathPlanner(space));
        using var pool = first.CreateWorkspaces(1, PathQueryBudget.Default);
        Assert.Throws<ArgumentException>(() => Begin(second, pool));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PoolRequiresAnExplicitPositiveCount(int count)
    {
        var planner = Incremental(Corridor());
        Assert.Throws<ArgumentOutOfRangeException>(() => planner.CreateWorkspaces(count, PathQueryBudget.Default));
    }

    [Theory]
    [InlineData(17, 3f)]
    [InlineData(16, 4f)]
    [InlineData(16, float.NaN)]
    public void BeginWithBudgetOutsideDeclaredLimitsIsRefusedBeforeLeasing(int maxExpandedNodes, float snapRadius)
    {
        var planner = Incremental(Corridor());
        var limit = new PathQueryBudget { MaxExpandedNodes = 16, SnapRadius = 3f };
        using var pool = planner.CreateWorkspaces(1, limit);
        var tooLarge = new PathQueryBudget { MaxExpandedNodes = maxExpandedNodes, SnapRadius = snapRadius };

        Assert.Throws<ArgumentException>(() => planner.BeginPath(Start, End(), 0f, tooLarge, pool));
        using var atLimit = planner.BeginPath(Start, End(), 0f, limit with { WalkOnly = true }, pool);
        Assert.Equal(NavPathStatus.Complete, Finish(atLimit, 1).Status);
        Assert.Equal(0, pool.Waits);
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("advance")]
    [InlineData("cancel")]
    [InlineData("dispose request")]
    [InlineData("dispose pool")]
    public void ReentryIsRefusedBeforeChangingOwnerOrQueue(string call)
    {
        BoundedThread.Start(() => CheckReentry(call)).Join();
    }

    static void CheckReentry(string call)
    {
        var planner = Incremental(Corridor());
        using var pool = planner.CreateWorkspaces(1, PathQueryBudget.Default);
        PathPlanRequest? waiter = null;
        var goal = new NavGoalRegion(End().Anchor, 0f, _ =>
        {
            Assert.Throws<InvalidOperationException>(() =>
            {
                switch (call)
                {
                    case "begin": using (Begin(planner, pool)) { } break;
                    case "advance": waiter!.Advance(new PathWorkBudget(1)); break;
                    case "cancel": waiter!.Cancel(); break;
                    case "dispose request": waiter!.Dispose(); break;
                    case "dispose pool": pool.Dispose(); break;
                }
            });
            return true;
        });
        using var owner = planner.BeginPath(Start, goal, 0f, PathQueryBudget.Default, pool);
        using var next = Begin(planner, pool);
        waiter = next;
        Finish(owner, int.MaxValue);
        Assert.Equal(PathPlanState.Pending, next.State);
        Assert.Equal(1, pool.Waits);
        Finish(next, int.MaxValue);
    }

    [Fact]
    public void PoolCanMoveThreadsButRefusesOverlappingCalls()
    {
        var planner = Incremental(Corridor());
        var pool = planner.CreateWorkspaces(1, PathQueryBudget.Default);
        var entered = new ManualResetEventSlim();
        var exit = new ManualResetEventSlim();
        var contenderFinished = new ManualResetEventSlim();
        var goal = new NavGoalRegion(End().Anchor, 0f, _ =>
        {
            entered.Set();
            exit.Wait();
            return true;
        });
        var request = planner.BeginPath(Start, goal, 0f, PathQueryBudget.Default, pool);
        var advance = BoundedThread.Start(() => request.Advance(new PathWorkBudget(int.MaxValue)));
        BoundedThread? contender = null;
        Exception? coordination = null;
        try
        {
            BoundedThread.Await(entered);
            contender = BoundedThread.Start(() =>
            {
                try
                {
                    Assert.Throws<InvalidOperationException>(() =>
                    {
                        using var unexpected = Begin(planner, pool);
                    });
                }
                finally { contenderFinished.Set(); }
            });
            BoundedThread.Await(contenderFinished);
        }
        catch (Exception error) { coordination = error; }
        exit.Set();
        // A thread that outlives its join can still use the request, the pool and the signals,
        // so they are released only after both threads stopped without a failure.
        BoundedThread.JoinAll(coordination, advance, contender);
        using (pool)
        using (entered)
        using (exit)
        using (contenderFinished)
        using (request)
        {
            Assert.Equal(PathPlanState.Ready, request.State);
            using var later = Begin(planner, pool);
            Finish(later, 7);
        }
    }

    [Fact]
    public void CancelledWaiterLeavesTheQueueAndCannotReleaseAnothersLease()
    {
        var planner = Incremental(Corridor());
        using var pool = planner.CreateWorkspaces(1, PathQueryBudget.Default);
        using var owner = Begin(planner, pool);
        using var cancelled = Begin(planner, pool);
        using var oldest = Begin(planner, pool);
        using var youngest = Begin(planner, pool);
        cancelled.Cancel();
        owner.Dispose();
        owner.Cancel();
        cancelled.Dispose();
        youngest.Advance(new PathWorkBudget(int.MaxValue));
        Assert.Equal(0, youngest.WorkSpent);
        Finish(oldest, int.MaxValue);
        Finish(youngest, int.MaxValue);
        Assert.Equal(PathPlanState.Cancelled, cancelled.State);
        Assert.Equal(3, pool.Waits);
    }

    [Fact]
    public void DisposingPoolCancelsAllAndRefusesLaterBegins()
    {
        var planner = Incremental(Corridor());
        using var pool = planner.CreateWorkspaces(2, PathQueryBudget.Default);
        using var first = Begin(planner, pool);
        using var second = Begin(planner, pool);
        using var waiter = Begin(planner, pool);
        first.Advance(new PathWorkBudget(1));
        pool.Dispose();
        pool.Dispose();
        foreach (var request in new[] { first, second, waiter })
        {
            Assert.Equal(PathPlanState.Cancelled, request.State);
            Assert.Null(request.Result);
        }
        Assert.Throws<ObjectDisposedException>(() => Begin(planner, pool));
    }

    [Fact]
    public void LaterOwnerHandsOverAWorkspaceWhileTheFirstOwnerIsPending()
    {
        var planner = Incremental(Corridor());
        using var pool = planner.CreateWorkspaces(2, PathQueryBudget.Default);
        Assert.Equal(2, pool.Count);
        using var first = Begin(planner, pool);
        using var second = Begin(planner, pool);
        using var third = Begin(planner, pool);
        var one = new PathWorkBudget(1);
        first.Advance(one);
        second.Advance(one);
        third.Advance(one);
        Assert.True(first.WorkSpent > 0);
        Assert.True(second.WorkSpent > 0);
        Assert.Equal(0, third.WorkSpent);
        Assert.Equal(1, pool.Waits);

        Finish(second, int.MaxValue);
        Assert.Equal(PathPlanState.Pending, first.State);
        third.Advance(one);
        Assert.True(third.WorkSpent > 0);
        Finish(third, int.MaxValue);
        Finish(first, int.MaxValue);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(8)]
    public void ThrowingPredicateCancelsAndReleasesExactlyOnce(int firstQuota)
    {
        var planner = Incremental(Corridor());
        using var pool = planner.CreateWorkspaces(1, PathQueryBudget.Default);
        var failure = new InvalidOperationException();
        var visits = new List<float>();
        var goal = new NavGoalRegion(End().Anchor, 1f, feet =>
        {
            visits.Add(feet.X);
            if (feet.X == 15.5f) throw failure;
            return true;
        });
        using var owner = planner.BeginPath(Start, goal, 0f, PathQueryBudget.Default, pool);
        using var next = Begin(planner, pool);
        using var later = Begin(planner, pool);

        // Both schedules reject x=13 before the cut. The eight-unit cut leaves x=14 for the throwing advance.
        owner.Advance(new PathWorkBudget(firstQuota));
        Assert.Equal(firstQuota, owner.WorkSpent);
        Assert.Equal(firstQuota == 12 ? new[] { 14.5f } : Array.Empty<float>(), visits);
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => owner.Advance(new PathWorkBudget(int.MaxValue))));
        Assert.Equal(12, owner.WorkSpent);
        Assert.Equal(new[] { 14.5f, 15.5f }, visits);
        Assert.Equal(PathPlanState.Cancelled, owner.State);
        owner.Cancel();
        owner.Dispose();
        Assert.Null(owner.Result);
        later.Advance(new PathWorkBudget(1));
        Assert.Equal(0, later.WorkSpent);
        Finish(next, int.MaxValue);
        Finish(later, int.MaxValue);
    }
}
