using System;
using System.Collections.Generic;
using System.Numerics;
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
    public void ColdSynchronousQueryKeepsBaseScratchAllocation()
    {
        // A clear point route rents scratch without enqueuing. Sixteen layers distinguish the
        // reference bool array from an int array even after runtime alignment.
        var layers = new NavGrid[16];
        Array.Fill(layers, Grid(2, 1, (_, _) => true));
        var space = new NavSpace(layers);
        var goal = Start with { X = 1.5f };
        var warmup = new GridPathPlanner(space);
        warmup.FindPath(Start, goal, 0f, PathQueryBudget.Default);

        // The reference is the base planner's cold allocation graph, built beside the planner on this runtime.
        (BaseColdScratch Scratch, NavPath Path) AllocateBase() =>
            (new BaseColdScratch(layers.Length * 2, layers.Length),
                new NavPath(NavPathStatus.Complete, new[] { new NavWaypoint(new Vector2(goal.X, goal.Z), 0) }));

        var reference = AllocateBase();
        long expectedBytes = AllocationMeasurement.Measure("base cold synchronous point query",
            () => { reference = AllocateBase(); });
        GC.KeepAlive(reference.Scratch);
        GC.KeepAlive(reference.Path);

        var planner = new GridPathPlanner(space);
        NavPath? result = null;
        long allocated = AllocationMeasurement.Measure("cold synchronous point query", () =>
            result = planner.FindPath(Start, goal, 0f, PathQueryBudget.Default));

        var path = Assert.IsType<NavPath>(result);
        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.Single(path.Waypoints);
        Assert.Equal(expectedBytes, allocated);
    }

    readonly record struct BaseMemberBox(int X, int Z, int Width, int Height, int Offset);

    // The fields and first allocations of the scratch a synchronous query rented before the kernel was shared.
    sealed class BaseColdScratch
    {
        internal readonly double[] Scores;
        internal readonly int[] Parents;
        internal readonly NavLinkKind?[] Incoming;
        internal readonly int[] Stamps;
        internal int Generation = 0;
        internal readonly BaseMemberBox[] Boxes;
        internal readonly bool[] Layers;
        internal bool[] Flags = Array.Empty<bool>();
        internal (int, int, int)[] Cells = Array.Empty<(int, int, int)>();
        internal NavGrid? RayGrid = null;
        internal float RayRadius = 0f;
        internal readonly Func<int, int, bool> RayBlocks;
        internal readonly PriorityQueue<int, (double F, int Seq)> Open = new();
        internal readonly List<int> Chain = new();
        internal readonly List<NavWaypoint> Waypoints = new();
        internal readonly List<Vector3> Members = new();
        internal readonly List<int> MemberNodeIds = new();
        internal (int, int, int, int) MemberBounds = default;

        internal BaseColdScratch(int nodes, int layers)
        {
            Scores = new double[nodes];
            Parents = new int[nodes];
            Incoming = new NavLinkKind?[nodes];
            Stamps = new int[nodes];
            Boxes = new BaseMemberBox[layers];
            Layers = new bool[layers];
            RayBlocks = (x, z) => !RayGrid!.IsPassable(x, z, RayRadius);
        }
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
        var (ordinaryAdvances, ordinaryBlocks) = Drive(ordinary, expectedWrapUnits: 0);
        EqualBits(expected, Assert.IsType<NavPath>(ordinary.Result));
        Assert.Equal(0, ordinaryBlocks);

        using var retry = NewRequest(planner, pool, goal);
        var (wrapAdvances, wrapBlocks) = Drive(retry, expectedWrapUnits: 16);
        EqualBits(expected, Assert.IsType<NavPath>(retry.Result));
        Assert.Equal(8, wrapBlocks);
        Assert.Equal(8, wrapAdvances - ordinaryAdvances);
        Assert.Equal(16, retry.WorkSpent - ordinary.WorkSpent);

        (int Advances, int Blocks) Drive(GridPathPlanner.GridSearchRequest request, int expectedWrapUnits)
        {
            var work = new PathWorkByKind();
            int advances = 0, blocks = 0;
            while (request.State == PathPlanState.Pending)
            {
                Assert.True(advances++ < 1000);
                if (request.NextPhaseForTests == Phase.Wrap) blocks++;
                request.AdvanceForTests(one, work);
            }
            Assert.Equal(expectedWrapUnits, work[PathWorkKind.Wrap]);
            Assert.Equal(request.WorkSpent, work.Total);
            return (advances, blocks);
        }
    }
}
