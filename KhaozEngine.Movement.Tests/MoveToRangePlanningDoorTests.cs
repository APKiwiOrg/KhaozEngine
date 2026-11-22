using System;
using System.Numerics;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using Xunit;
using static KhaozEngine.Tests.Movement.SegmentAdmissionFixture;

namespace KhaozEngine.Tests.Movement;

public sealed partial class MoveToRangePlanningTests
{
    [Fact]
    public void FollowerRefusesTheWrongSpaceForACrossLayerDrop()
    {
        var (space, wrongSpace) = CrossLayerDropSpaces();
        var planner = new GridPathPlanner(space);
        using var pool = ((IIncrementalRegionPathPlanner)planner).CreateWorkspaces(1, PathQueryBudget.Default);
        var follow = Follow(pool, int.MaxValue);
        Assert.Throws<ArgumentException>("space", () => new PathFollower(planner, follow, wrongSpace));
        _ = new PathFollower(planner, space: wrongSpace);
    }

    [Fact]
    public void FollowerUsesThePlannersSpaceForACrossLayerDropWhenOmitted()
    {
        var (space, _) = CrossLayerDropSpaces();
        var planner = new GridPathPlanner(space);
        using var pool = ((IIncrementalRegionPathPlanner)planner).CreateWorkspaces(1, PathQueryBudget.Default);
        var follower = new PathFollower(planner, Follow(pool, int.MaxValue));
        var output = follower.Tick(new(0.5f, 4f, 0.5f), Goal(new(1.5f, 0f, 0.5f)), 0f, 0f);

        Assert.Equal(PathFollowState.Dropping, output.State);
        Assert.Equal(new Vector3(0.5f, 4f, 0.5f), output.DropTraversal?.StartFeet);
        Assert.Equal(new Vector3(1.5f, 0f, 0.5f), output.DropTraversal?.LandingFeet);
    }

    [Fact]
    public void MoverOverloadsRefuseTheWrongSpaceForACrossLayerDrop()
    {
        var (space, wrongSpace) = CrossLayerDropSpaces();
        var planner = new GridPathPlanner(space);
        using var pool = ((IIncrementalRegionPathPlanner)planner).CreateWorkspaces(1, PathQueryBudget.Default);
        var follow = Follow(pool, int.MaxValue);

        Assert.Throws<ArgumentException>("space", () => new MoveToRange(planner, wrongSpace, (_, _) => true, follow));
        Assert.Throws<ArgumentException>("space", () =>
            new MoveToRange(planner, wrongSpace, (_, _) => true, follow, RouteApproachOptions.Default));
        _ = new MoveToRange(planner, wrongSpace, (_, _) => true);
        _ = new MoveToRange(planner, wrongSpace, (_, _) => true, null, RouteApproachOptions.Default);
    }

    [Fact]
    public void DelegateMoverWithItsOwnRawPoolRefusesSlicedStraightening()
    {
        var space = Flat(3, 1, Tuning.CapsuleRadius).Space;
        var planner = new GridPathPlanner(space);
        using var pool = ((IIncrementalRegionPathPlanner)planner).CreateWorkspaces(1, PathQueryBudget.Default);
        var follow = Follow(pool, 6);
        _ = new MoveToRange(planner, space, (_, _) => true, follow);

        Assert.Throws<ArgumentException>("follow", () => new MoveToRange(planner, space, (_, _) => true, follow, Straight));
    }

    static (NavSpace Space, NavSpace WrongSpace) CrossLayerDropSpaces()
    {
        var upper = NavGrid.FromBlockedSurfaces(1, 1, 1f, 0f, 0f, [false], [4f]);
        var lower = NavGrid.FromBlockedSurfaces(1, 1, 1f, 1f, 0f, [false], [0f]);
        var drop = new NavLink(0, 0, 0, 1, 0, 0) { Kind = NavLinkKind.Drop };
        return (new NavSpace([upper, lower], [drop]), NavSpace.Single(upper));
    }
}
