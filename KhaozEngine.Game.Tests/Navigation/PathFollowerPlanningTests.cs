using System;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;
using static KhaozEngine.Tests.Navigation.DisconnectedPlannerFixture;
using static KhaozEngine.Tests.Navigation.GridPathPlannerDisconnectedProgressTests;
using static KhaozEngine.Tests.Navigation.GridSearchRequestFixtures;

namespace KhaozEngine.Tests.Navigation;

public sealed partial class PathFollowerPlanningTests
{
    // ExpansionCapHasALiteralTrace: snap, region, certificate, seed, two expansions and one waypoint.
    static readonly int[] PartialTrace =
    [
        2, 2, 2, 2, 1, 4, 1, 4, 1,
        6, 6, 6, 6, 6, 6, 6, 6,
        4, 1, 6, 6, 6, 6, 6, 6, 6, 6,
        1, 1, 1, 2, 2, 1,
    ];

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(31)]
    [InlineData(128)]
    public void PublishCallDependsOnlyOnInputsQuotasAndPool(int quota)
    {
        var planner = Corridor(3);
        int slices = Slices(quota, PartialTrace);
        for (int replay = 0; replay < 2; replay++)
        {
            using var pool = Incremental(planner).CreateWorkspaces(1, Budget(2));
            var first = Follower(planner, pool, quota);
            var second = Follower(planner, pool, quota);
            int firstPublish = slices == 1 ? 1 : slices + 1;
            // Only the first owner begins with a free lease. A queued owner adopts on a later call
            // even if all its work fits its first advance with the workspace.
            int secondPublish = slices == 1 ? 1 : 2 * slices;
            for (int call = 1; call <= secondPublish; call++)
            {
                AssertPartialAt(first, call, firstPublish);
                AssertPartialAt(second, call, secondPublish);
            }
            Assert.Equal(slices == 1 ? 0 : 1, pool.Waits);
        }
    }

    [Fact]
    public void MultiCallRequestAdoptsOnTheNextCall()
    {
        var planner = Corridor(3);
        using var pool = Incremental(planner).CreateWorkspaces(1, Budget(2));
        var follower = Follower(planner, pool, 6);
        for (int call = 1; call <= 23; call++)
            Waiting(follower, follower.Tick(Start, End(3), 0f, 1f));
        // The final slice released the lease, even though the follower has yet to adopt.
        using var next = Incremental(planner).BeginPath(Start, End(3), 0f, Budget(2), pool);
        next.Advance(new(128));
        Assert.Equal(PathPlanState.Ready, next.State);
        AssertPartialAt(follower, 24, 24);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MovingTargetCannotStarve(bool vertical)
    {
        var planner = Corridor(3);
        using var pool = Incremental(planner).CreateWorkspaces(1, Budget(2));
        var follower = Follower(planner, pool, 6);
        var member = End(3);
        for (int call = 1; call <= 23; call++)
        {
            Vector3 anchor = vertical ? member.Anchor with { Y = call * 2f } : member.Anchor with { X = call * 2f };
            var goal = call == 1 ? member : new NavGoalRegion(anchor, 50f, _ => false);
            Waiting(follower, follower.Tick(Start, goal, 0f, 1f));
        }
        var drifted = new NavGoalRegion(vertical ? new(2.5f, 10f, 0.5f) : new(10f, 0f, 0.5f), 16f, _ => false);
        AssertPartial(follower, follower.Tick(Start, drifted, 0f, 1f));
        AssertPartial(follower, follower.Tick(Start, drifted, 0f, 0.25f));
        Waiting(follower, follower.Tick(Start, drifted, 0f, 0.25f));
    }

    [Theory]
    [InlineData(0.5f, 0f, true)]
    [InlineData(0.625f, -0f, true)]
    [InlineData(1.5f, 0f, false)]
    [InlineData(0.5f, 4f, false)]
    [InlineData(-5f, 0f, false)]
    public void AdoptionUsesTheFrozenStartNode(float x, float y, bool adopt)
    {
        var space = new NavSpace([Grid(3, 1, (_, _) => true), Grid(3, 1, (_, _) => true, 4f)]);
        var planner = new GridPathPlanner(space);
        using var pool = Incremental(planner).CreateWorkspaces(1, Budget(2));
        var follower = Follower(planner, pool, 6, space);
        // The second layer adds two layer probes, a member box and two candidate cells.
        int slices = Slices(6, [2, 2, 2, 2, 2, .. PartialTrace]);
        var goal = End(3);
        for (int call = 1; call <= slices; call++)
            Waiting(follower, follower.Tick(Start, goal, 0f, 0f));
        var result = follower.Tick(new(x, y, 0.5f), goal, 0f, 0f);
        if (adopt)
            AssertPartial(follower, result);
        else
        {
            Waiting(follower, result);
            // Another begin in the same call has already taken the just-released lease.
            using var queued = Incremental(planner).BeginPath(Start, goal, 0f, Budget(2), pool);
            Assert.Equal(1, pool.Waits);
        }
    }

    [Theory]
    [InlineData("point")]
    [InlineData("membership")]
    [InlineData("reset")]
    public void PointMembershipAndResetReleaseBeforeAnotherOwnerAdvances(string cause)
    {
        var planner = Corridor(3);
        using var pool = Incremental(planner).CreateWorkspaces(1, Budget(2));
        var owner = Follower(planner, pool, 6);
        Waiting(owner, owner.Tick(Start, End(3), 0f, 0f));
        using var waiter = Incremental(planner).BeginPath(Start, End(3), 0f, Budget(2), pool);
        waiter.Advance(new(128));
        Assert.Equal(0, waiter.WorkSpent);

        switch (cause)
        {
            case "point":
                Assert.Equal(PathFollowState.Following, owner.Tick(Start, End(3).Anchor, 0f, 0f).State);
                break;
            case "membership":
                Assert.Equal(PathFollowState.Arrived, owner.Tick(Start, Member(Start), 0f, 0f).State);
                break;
            case "reset":
                owner.Reset();
                break;
        }

        Assert.False(owner.PlanningPending);
        waiter.Advance(new(128));
        Assert.Equal(PathPlanState.Ready, waiter.State);
    }

    [Fact]
    public void ResetDequeuesTheWaitingFollower()
    {
        var planner = Corridor(3);
        var incremental = Incremental(planner);
        using var pool = incremental.CreateWorkspaces(1, Budget(2));
        using var owner = incremental.BeginPath(Start, End(3), 0f, Budget(2), pool);
        var follower = Follower(planner, pool, 6);
        Waiting(follower, follower.Tick(Start, End(3), 0f, 0f));
        using var next = incremental.BeginPath(Start, End(3), 0f, Budget(2), pool);

        follower.Reset();
        owner.Cancel();
        next.Advance(new(128));
        Assert.False(follower.PlanningPending);
        Assert.Equal(PathPlanState.Ready, next.State);
    }

    static IIncrementalRegionPathPlanner Incremental(GridPathPlanner planner) => planner;

    static PathFollower Follower(GridPathPlanner planner, PathPlanWorkspaces pool, int quota, NavSpace? space = null)
        => new(planner, new PathFollowConfig { Budget = Budget(2), Planning = new(new(quota), pool) }, space);

    static int Slices(int quota, int[] trace)
    {
        int slices = 0;
        int used = 0;
        foreach (int operation in trace)
        {
            if (used == 0 || operation > quota - used)
            {
                slices++;
                used = 0;
            }
            used += operation;
        }
        return slices;
    }

    static void AssertPartialAt(PathFollower follower, int call, int publish)
    {
        var output = follower.Tick(Start, End(3), 0f, 0f);
        if (call < publish) Waiting(follower, output);
        else AssertPartial(follower, output);
    }

    static void Waiting(PathFollower follower, PathFollowOutput output)
    {
        Assert.Equal(PathFollowState.WaitingForPath, output.State);
        Bits(Vector2.Zero, output.WorldDir);
        Bits(Vector2.Zero, output.ActiveWaypoint);
        Assert.Null(follower.ActivePath);
        Assert.True(follower.PlanningPending);
    }

    static void AssertPartial(PathFollower follower, PathFollowOutput output)
    {
        Assert.False(follower.PlanningPending);
        Assert.Equal(PathFollowState.Following, output.State);
        Bits(Vector2.UnitX, output.WorldDir);
        Bits(new(1.5f, 0.5f), output.ActiveWaypoint);
        var path = Assert.IsType<NavPath>(follower.ActivePath);
        Assert.Equal(NavPathStatus.Partial, path.Status);
        var step = Assert.Single(path.Waypoints);
        Bits(new(1.5f, 0.5f), step.Position);
        Assert.Equal(0, step.Layer);
        Assert.Equal(NavWaypointKind.Walk, step.Kind);
    }

    static void Bits(Vector2 expected, Vector2 actual)
    {
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.X), BitConverter.SingleToInt32Bits(actual.X));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.Y), BitConverter.SingleToInt32Bits(actual.Y));
    }
}
