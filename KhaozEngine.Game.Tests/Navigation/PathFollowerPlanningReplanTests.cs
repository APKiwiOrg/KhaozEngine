using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;
using static KhaozEngine.Tests.Navigation.DisconnectedPlannerFixture;
using static KhaozEngine.Tests.Navigation.GridPathPlannerDisconnectedProgressTests;
using static KhaozEngine.Tests.Navigation.GridSearchRequestFixtures;

namespace KhaozEngine.Tests.Navigation;

public sealed partial class PathFollowerPlanningTests
{
    [Fact]
    public void ExhaustedPartialWaitsForCooldownAndCanBeFollowedByARefusal()
    {
        var planner = Corridor(3);
        using var pool = Incremental(planner).CreateWorkspaces(1, Budget(2));
        var follower = Follower(planner, pool, 6);
        for (int call = 1; call <= 24; call++)
            AssertPartialAt(follower, call, 24);

        Vector3 terminal = new(1.5f, 0f, 0.5f);
        var empty = new NavGoalRegion(End(3).Anchor, 0f, _ => false);
        Assert.Equal(PathFollowState.WaitingForPath, follower.Tick(terminal, empty, 0f, 0.25f).State);
        Assert.False(follower.PlanningPending);

        // Snap, member box, two negative membership tests. Three operations fit first, one next.
        Waiting(follower, follower.Tick(terminal, empty, 0f, 0.25f));
        Waiting(follower, follower.Tick(terminal, empty, 0f, 0.5f));
        Assert.Equal(PathFollowState.Unreachable, follower.Tick(terminal, empty, 0f, 0.5f).State);
        Assert.False(follower.PlanningPending);
        Assert.Null(follower.ActivePath);
        Assert.Equal(PathFollowState.Unreachable, follower.Tick(terminal, empty, 0f, 0.25f).State);
        Assert.False(follower.PlanningPending);
        Waiting(follower, follower.Tick(terminal, empty, 0f, 0.25f));
    }

    [Fact]
    public void CooldownExpiryAloneKeepsTheAdoptedCorridor()
    {
        var planner = Corridor(3);
        using var pool = Incremental(planner).CreateWorkspaces(1, Budget(2));
        var follower = Follower(planner, pool, 6);
        for (int call = 1; call <= 24; call++)
            AssertPartialAt(follower, call, 24);

        NavPath? adopted = follower.ActivePath;
        AssertPartial(follower, follower.Tick(Start, End(3), 0f, 2f));
        Assert.Same(adopted, follower.ActivePath);
    }

    [Fact]
    public void ReachedCompleteTerminalRefreshesBeforeCooldown()
    {
        var planner = Corridor(3);
        using var pool = Incremental(planner).CreateWorkspaces(1, Budget(2));
        var follower = Follower(planner, pool, 6);
        var near = End(2);
        // A two-node complete result pays for the start's neighbours but none at the goal.
        int publish = Slices(6, [2, 2, 2, 2, 2, 1, 4, 1, 4, 1, 6, 6, 6, 6, 6, 6, 6, 6,
            4, 1, 1, 1, 1, 2, 2, 1]) + 1;
        for (int call = 1; call < publish; call++)
            Waiting(follower, follower.Tick(Start, near, 0f, 0f));

        Assert.Equal(PathFollowState.Following, follower.Tick(Start, near, 0f, 0f).State);
        Assert.Equal(NavPathStatus.Complete, follower.ActivePath?.Status);
        var changed = new NavGoalRegion(near.Anchor, 0f, _ => false);
        Waiting(follower, follower.Tick(near.Anchor, changed, 0f, 0f));
    }
}
