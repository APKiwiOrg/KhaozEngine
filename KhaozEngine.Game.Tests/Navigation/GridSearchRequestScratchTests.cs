using System;
using KhaozEngine.Navigation;
using Xunit;
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
        using (planner.CreateWorkspaces(1)) { }

        long creation = AllocationMeasurement.Measure("two prewarmed search workspaces", () =>
        {
            using var pool = planner.CreateWorkspaces(2);
        });
        // The stamps, scores and parent ids alone require 16 bytes per cell per workspace.
        Assert.True(creation >= 2L * cells * (sizeof(int) * 2 + sizeof(double)));
        using var workspaces = planner.CreateWorkspaces(2);
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
        using var pool = planner.CreateWorkspaces(2);
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
    public void GenerationWrapClearsInSlicesAndCanBeCancelledPartway()
    {
        var grid = Corridor(2048);
        var planner = Incremental(grid);
        using var pool = grid.CreateWorkspacesForTests(1, int.MaxValue - 3);
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

        using var retry = NewRequest(planner, pool, goal);
        int blocks = 0;
        for (int i = 0; retry.State == PathPlanState.Pending; i++)
        {
            Assert.True(i < 1000);
            if (retry.NextPhaseForTests == Phase.Wrap) blocks++;
            retry.Advance(one);
        }
        Assert.True(blocks > 1);
        EqualBits(expected, Assert.IsType<NavPath>(retry.Result));
    }
}
