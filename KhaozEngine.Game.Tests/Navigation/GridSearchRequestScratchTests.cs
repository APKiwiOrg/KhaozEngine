using System;
using KhaozEngine.Navigation;
using Xunit;
using static KhaozEngine.Tests.Navigation.DisconnectedPlannerFixture;
using static KhaozEngine.Tests.Navigation.GridPathPlannerDisconnectedProgressTests;
using static KhaozEngine.Tests.Navigation.GridSearchRequestEqualityTests;
using static KhaozEngine.Tests.Navigation.GridSearchRequestFixtures;
using Phase = KhaozEngine.Navigation.GridPathPlanner.SearchPhase;

namespace KhaozEngine.Tests.Navigation;

[Collection("AllocSensitive")]
public class GridSearchRequestScratchTests
{
    [Fact]
    public void PoolCreationAllocatesBothFullWorkspaces()
    {
        const int cells = 10_000;
        var planner = Incremental(Corridor(cells));
        using (planner.CreateWorkspaces(1, PathQueryBudget.Default)) { }

        long creation = AllocationMeasurement.Measure("two prewarmed search workspaces", () =>
        {
            using var pool = planner.CreateWorkspaces(2, PathQueryBudget.Default);
        });
        // The stamps, scores and parent ids alone require 16 bytes per cell per workspace.
        Assert.True(creation >= 2L * cells * (sizeof(int) * 2 + sizeof(double)));
        using var workspaces = planner.CreateWorkspaces(2, PathQueryBudget.Default);
        NavGoalRegion goal = End();
        PathPlanRequest? first = null, second = null;
        try
        {
            long begins = AllocationMeasurement.Measure("two request handles", () =>
            {
                first = planner.BeginPath(Start, goal, 0f, PathQueryBudget.Default, workspaces);
                second = planner.BeginPath(Start, goal, 0f, PathQueryBudget.Default, workspaces);
            });
            Assert.InRange(begins, 1, cells - 1);
        }
        finally
        {
            first?.Dispose();
            second?.Dispose();
        }
    }

    [Fact]
    public void ParkedRequestsKeepWarmedSynchronousScratchAndRequestsAllocateOnlyResults()
    {
        var planner = Incremental(Corridor(10_000));
        using var pool = planner.CreateWorkspaces(2, PathQueryBudget.Default);
        NavGoalRegion goal = End();
        using var warmFirst = NewRequest(planner, pool, goal);
        using var warmSecond = NewRequest(planner, pool, goal);
        NavPath expected = Finish(warmFirst, 7);
        Finish(warmSecond, 7);
        planner.FindPath(Start, goal, 0f, PathQueryBudget.Default);
        using var first = NewRequest(planner, pool, goal);
        using var second = NewRequest(planner, pool, goal);
        var firstSlice = new PathWorkBudget(1);
        first.Advance(firstSlice);
        second.Advance(firstSlice);
        NavPath MakePath() => new(expected.Status, new NavWaypoint[expected.Waypoints.Count]);
        MakePath();
        long result = AllocationMeasurement.Measure("waypoint array, view and path", () => GC.KeepAlive(MakePath()));
        long synchronous = AllocationMeasurement.Measure("synchronous query with two pending requests", () =>
            GC.KeepAlive(planner.FindPath(Start, goal, 0f, PathQueryBudget.Default)));
        var whole = new PathWorkBudget(int.MaxValue);
        long request = AllocationMeasurement.Measure("request advance with a warmed workspace", () => first.Advance(whole));
        Assert.Equal(result, synchronous);
        Assert.Equal(result, request);
        EqualBits(expected, Assert.IsType<NavPath>(first.Result));
        EqualBits(expected, Finish(second, 7));
    }

    [Fact]
    public void ColdAndLargerRequestsAllocateOnlyTheirResults()
    {
        const int length = 1025;
        var grid = Grid(length + 2, 256, (x, z) => x < length && z == 0 || x == length + 1);
        var planner = Incremental(new GridPathPlanner(NavSpace.Single(grid)));
        NavGoalRegion small = End();
        var large = new NavGoalRegion(End(length).Anchor, length,
            feet => feet.X == length - 0.5f && feet.Z == 0.5f || feet.X == length + 1.5f && feet.Z < 8f);
        PathQueryBudget smallBudget = Budget(16), largeBudget = Budget(length);
        long smallResultBytes = MeasureResult(15);
        long largeResultBytes = MeasureResult(length - 1);

        using var pool = planner.CreateWorkspaces(1, largeBudget);
        long cold = MeasureRequest(small, smallBudget, 15);
        long later = MeasureRequest(large, largeBudget, length - 1);
        Assert.Equal(smallResultBytes, cold);
        Assert.Equal(largeResultBytes, later);

        long MeasureRequest(NavGoalRegion goal, PathQueryBudget budget, int waypoints)
        {
            using var request = planner.BeginPath(Start, goal, 0f, budget, pool);
            var whole = new PathWorkBudget(int.MaxValue);
            long bytes = AllocationMeasurement.Measure("advance on the prewarmed workspace", () => request.Advance(whole));
            NavPath path = Assert.IsType<NavPath>(request.Result);
            Assert.Equal(NavPathStatus.Complete, path.Status);
            Assert.Equal(waypoints, path.Waypoints.Count);
            return bytes;
        }

        static long MeasureResult(int waypoints)
        {
            NavPath MakePath() => new(NavPathStatus.Complete, new NavWaypoint[waypoints]);
            GC.KeepAlive(MakePath());
            return AllocationMeasurement.Measure("waypoint array, view and path", () => GC.KeepAlive(MakePath()));
        }
    }

    [Fact]
    public void GenerationWrapClearsInSlicesAndCanBeCancelledPartway()
    {
        var grid = Corridor(2048);
        var planner = Incremental(grid);
        using var pool = grid.CreateWorkspacesForTests(1, PathQueryBudget.Default, int.MaxValue - 3);
        NavGoalRegion goal = End();
        using var previous = NewRequest(planner, pool, goal);
        NavPath expected = Finish(previous, int.MaxValue);

        using var cut = NewRequest(planner, pool, goal);
        var one = new PathWorkBudget(1);
        for (int i = 0; cut.NextPhaseForTests != Phase.Wrap; i++)
        {
            Assert.True(i < 100 && cut.State == PathPlanState.Pending, "stamp wrap did not begin");
            cut.Advance(one);
        }
        cut.Advance(one);
        Assert.Equal(Phase.Wrap, cut.NextPhaseForTests);
        cut.Cancel();

        using var ordinaryPool = planner.CreateWorkspaces(1, PathQueryBudget.Default);
        using var ordinary = NewRequest(planner, ordinaryPool, goal);
        var (ordinaryAdvances, ordinaryBlocks) = Drive(ordinary);
        EqualBits(expected, Assert.IsType<NavPath>(ordinary.Result));
        Assert.Equal(0, ordinaryBlocks);

        using var retry = NewRequest(planner, pool, goal);
        var (wrapAdvances, wrapBlocks) = Drive(retry);
        EqualBits(expected, Assert.IsType<NavPath>(retry.Result));
        Assert.Equal(8, wrapBlocks);
        Assert.Equal(8, wrapAdvances - ordinaryAdvances);
        Assert.Equal(16, retry.WorkSpent - ordinary.WorkSpent);

        (int Advances, int Blocks) Drive(GridPathPlanner.GridSearchRequest request)
        {
            int advances = 0, blocks = 0;
            while (request.State == PathPlanState.Pending)
            {
                Assert.True(advances++ < 1000);
                if (request.NextPhaseForTests == Phase.Wrap) blocks++;
                request.Advance(one);
            }
            return (advances, blocks);
        }
    }
}
