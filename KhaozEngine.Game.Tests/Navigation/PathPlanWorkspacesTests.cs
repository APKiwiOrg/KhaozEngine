using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Navigation;
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
        using var pool = planner.CreateWorkspaces(1);
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
        using var pool = first.CreateWorkspaces(1);
        Assert.Throws<ArgumentException>(() => Begin(second, pool));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PoolRequiresAnExplicitPositiveCount(int count)
    {
        var planner = Incremental(Corridor());
        Assert.Throws<ArgumentOutOfRangeException>(() => planner.CreateWorkspaces(count));
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("advance")]
    [InlineData("cancel")]
    [InlineData("dispose request")]
    [InlineData("dispose pool")]
    public void ReentryIsRefusedBeforeChangingOwnerOrQueue(string call)
    {
        var planner = Incremental(Corridor());
        using var pool = planner.CreateWorkspaces(1);
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
    public async Task PoolCanMoveThreadsButRefusesOverlappingCalls()
    {
        var planner = Incremental(Corridor());
        using var pool = planner.CreateWorkspaces(1);
        using var entered = new ManualResetEventSlim();
        using var exit = new ManualResetEventSlim();
        var goal = new NavGoalRegion(End().Anchor, 0f, _ =>
        {
            entered.Set();
            exit.Wait();
            return true;
        });
        using var request = planner.BeginPath(Start, goal, 0f, PathQueryBudget.Default, pool);
        Task advance = Task.Run(() => request.Advance(new PathWorkBudget(int.MaxValue)));
        try
        {
            entered.Wait();
            Assert.Throws<InvalidOperationException>(() => Begin(planner, pool));
        }
        finally
        {
            exit.Set();
            await advance;
        }
        Assert.Equal(PathPlanState.Ready, request.State);
        using var later = Begin(planner, pool);
        Finish(later, 7);
    }

    [Fact]
    public void CancelledWaiterLeavesTheQueueAndCannotReleaseAnothersLease()
    {
        var planner = Incremental(Corridor());
        using var pool = planner.CreateWorkspaces(1);
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
        using var pool = planner.CreateWorkspaces(2);
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
    public void NPlusOneRequestsUseNWorkspacesAndHandOverInBeginOrder()
    {
        var planner = Incremental(Corridor());
        using var pool = planner.CreateWorkspaces(2);
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

        Finish(first, int.MaxValue);
        Finish(second, int.MaxValue);
        Finish(third, int.MaxValue);
    }

    [Fact]
    public void ThrowingPredicateCancelsAndReleasesExactlyOnce()
    {
        var planner = Incremental(Corridor());
        using var pool = planner.CreateWorkspaces(1);
        var failure = new InvalidOperationException();
        var goal = new NavGoalRegion(End().Anchor, 0f, _ => throw failure);
        using var owner = planner.BeginPath(Start, goal, 0f, PathQueryBudget.Default, pool);
        using var next = Begin(planner, pool);
        using var later = Begin(planner, pool);

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => owner.Advance(new PathWorkBudget(int.MaxValue))));
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
