using System;
using System.Numerics;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.Movement.SegmentAdmissionFixture;

namespace KhaozEngine.Tests.Movement;

[Collection("AllocSensitive")]
public sealed class MoveToRangePlanningAllocationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BeginAllocatesOnlyTheHandleAndAdmission(bool straighten)
    {
        var navigation = Flat(12, 3, 0.25f);
        IIncrementalRegionPathPlanner planner = straighten
            ? new RouteStraightener(navigation)
            : (IIncrementalRegionPathPlanner)navigation.Planner;
        using var pool = navigation.CreatePlanningWorkspaces(1);
        var start = new Vector3(0.5f, 0f, 1.5f);
        var goal = Goal(new(11.5f, 0f, 1.5f));

        PathPlanRequest Begin() => planner.BeginPath(start, goal, 0.25f, PathQueryBudget.Default, pool);

        Begin().Dispose();
        PathPlanRequest? active = null;
        long leased = AllocationMeasurement.Measure("leased path begin", () => active = Begin());
        using var owner = Assert.IsAssignableFrom<PathPlanRequest>(active);
        PathPlanRequest? waiting = null;
        Begin().Dispose(); // Warm the queue insertion and removal while a workspace is leased.
        long queued = AllocationMeasurement.Measure("queued path begin", () => waiting = Begin());
        using var waiter = Assert.IsAssignableFrom<PathPlanRequest>(waiting);
        Assert.Equal(2, pool.Waits);

        output.WriteLine($"Leased and queued begin: {leased} and {queued} bytes");
        // Handles contain the profile endpoint cursor and, for straightening, the segment cursor.
        Assert.InRange(leased, 1, straighten ? 1448 : 800);
        Assert.Equal(leased, queued);

        owner.Advance(new(6));
        AllocationMeasurement.NoPerCallAllocation("warmed pending path advance", () => owner.Advance(new(6)));
        Assert.Equal(PathPlanState.Pending, owner.State);
        Assert.Null(owner.Result);
    }

    [Fact]
    public void WrongPlanningSpaceIsRefusedBeforeTheStallBufferIsAllocated()
    {
        NavSpace space = Flat(3, 1, 0.25f).Space;
        NavSpace otherSpace = Flat(3, 1, 0.25f).Space;
        var planner = new GridPathPlanner(space);
        using var pool = ((IIncrementalRegionPathPlanner)planner).CreateWorkspaces(1, PathQueryBudget.Default);
        var follow = new PathFollowConfig { Planning = new(new(7), pool) };
        // A window this long makes the stall buffer far larger than everything else a refused construction allocates.
        var options = new RouteApproachOptions { Stall = new RouteStallOptions(65_535, 0.1f) };
        bool refused = false;

        long allocated = AllocationMeasurement.Measure("refused planning space", () =>
        {
            try
            {
                _ = new MoveToRange(planner, otherSpace, (_, _) => true, follow, options);
            }
            catch (ArgumentException error) when (error.ParamName == "space")
            {
                refused = true;
            }
        });

        output.WriteLine($"refused planning space allocated {allocated} bytes");
        Assert.True(refused);
        Assert.True(allocated < 65_536, $"A refused planning space allocated {allocated} bytes, so the stall buffer was built first.");
    }
}
