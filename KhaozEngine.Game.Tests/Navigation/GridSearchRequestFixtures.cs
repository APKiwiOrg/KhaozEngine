using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;
using static KhaozEngine.Tests.Navigation.DisconnectedPlannerFixture;
using static KhaozEngine.Tests.Navigation.GridPathPlannerDisconnectedProgressTests;

namespace KhaozEngine.Tests.Navigation;

internal static class GridSearchRequestFixtures
{
    internal static readonly Vector3 Start = new(0.5f, 0f, 0.5f);

    internal static GridPathPlanner Corridor(int length = 16)
        => new(NavSpace.Single(Grid(length, 1, (_, _) => true)));

    internal static NavGoalRegion End(int length = 16) => Member(new Vector3(length - 0.5f, 0f, 0.5f));

    internal static IIncrementalRegionPathPlanner Incremental(GridPathPlanner planner)
        => Assert.IsAssignableFrom<IIncrementalRegionPathPlanner>(planner);

    internal static NavPath Finish(PathPlanRequest request, int quantum)
    {
        for (int advance = 0; request.State == PathPlanState.Pending; advance++)
        {
            Assert.True(advance < 2_000_000, "the request did not finish");
            long before = request.WorkSpent;
            request.Advance(new PathWorkBudget(quantum));
            if (request.State == PathPlanState.Pending) Assert.True(request.WorkSpent > before);
        }
        Assert.Equal(PathPlanState.Ready, request.State);
        return Assert.IsType<NavPath>(request.Result);
    }

    internal static void EqualBits(NavPath expected, NavPath actual)
    {
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Waypoints.Count, actual.Waypoints.Count);
        for (int i = 0; i < expected.Waypoints.Count; i++)
        {
            NavWaypoint want = expected.Waypoints[i], got = actual.Waypoints[i];
            Assert.Equal(want.Kind, got.Kind);
            Assert.Equal(want.Layer, got.Layer);
            Assert.Equal(BitConverter.SingleToInt32Bits(want.Position.X), BitConverter.SingleToInt32Bits(got.Position.X));
            Assert.Equal(BitConverter.SingleToInt32Bits(want.Position.Y), BitConverter.SingleToInt32Bits(got.Position.Y));
        }
    }

    internal static IEnumerable<(GridPathPlanner Planner, Vector3 Start, NavGoalRegion Goal,
        PathQueryBudget Budget, NavPathStatus Status)> TargetedQueries()
    {
        var corridor = Corridor();
        yield return (corridor, Start, End(), Budget(1), NavPathStatus.Unreachable);
        yield return (corridor, Start, End(), Budget(8), NavPathStatus.Partial);
        yield return (corridor, Start, new NavGoalRegion(End().Anchor, 1f, _ => false), Budget(8), NavPathStatus.Unreachable);
        yield return (corridor, Start with { X = -0.5f }, End(), Budget(8), NavPathStatus.Unreachable);
        yield return (corridor, Start with { X = -0.5f }, End(),
            Budget(64) with { SnapRadius = 2f }, NavPathStatus.Complete);
        yield return (Corridor(4100), Start, End(4100), Budget(4096), NavPathStatus.Partial);

        var upper = Grid(1, 1, (_, _) => true, 4f);
        var lower = Grid(1, 1, (_, _) => true);
        foreach (NavLinkKind kind in new[] { NavLinkKind.Hop, NavLinkKind.Drop })
        {
            var link = new NavLink(0, 0, 0, 1, 0, 0) { Kind = kind };
            var planner = new GridPathPlanner(new NavSpace(new[] { upper, lower }, new[] { link }));
            yield return (planner, Start with { Y = 4f }, Member(Start), Budget(8), NavPathStatus.Complete);
            yield return (planner, Start with { Y = 4f }, Member(Start),
                Budget(8) with { WalkOnly = true }, NavPathStatus.Unreachable);
        }

        // Equal-price links into the same node retain the selected incoming edge.
        var stair = new NavLink(0, 0, 0, 1, 0, 0);
        var equalSpace = new NavSpace(new[] { upper, lower }, new[] { stair with { Kind = NavLinkKind.Hop }, stair });
        yield return (new GridPathPlanner(equalSpace, hopCostCells: 1f), Start with { Y = 4f }, Member(Start),
            Budget(8), NavPathStatus.Complete);

        var yaw = NavGrid.FromSurfaces(8, 3, 1f, 24f, -13f,
            (_, _) => new NavSurfaceSample(true, 0f, 2f), 0.5f, 1f, -1f, 2f, 0.37f);
        Vector2 from = yaw.CellCenter(0, 0), to = yaw.CellCenter(7, 2);
        yield return (new GridPathPlanner(NavSpace.Single(yaw)), new(from.X, 0f, from.Y),
            Member(new Vector3(to.X, 0f, to.Y)), Budget(64), NavPathStatus.Complete);

        var layered = new NavSpace(new[] { Grid(8, 1, (_, _) => true), Grid(8, 1, (_, _) => true, 4f) });
        yield return (new GridPathPlanner(layered), Start,
            new NavGoalRegion(End(8).Anchor, 0f, feet => feet.X == 7.5f), Budget(64), NavPathStatus.Complete);

        foreach ((int nodes, int members) in new[] { (32, 2), (32_769, 1), (1025, 256) })
        {
            NavGrid grid = Grid(nodes + 2, members, (x, z) => x < nodes && z == 0 || x == nodes + 1);
            var goal = new NavGoalRegion(new Vector3(nodes + 1.5f, 0f, 0.5f), members,
                feet => feet.X == nodes + 1.5f);
            yield return (new GridPathPlanner(NavSpace.Single(grid)), Start with { X = nodes - 0.5f },
                goal, Budget(8), NavPathStatus.Unreachable);
        }

        // A provisional queue entry for the goal is improved by a cheaper parallel edge. The goal
        // is outside that component so the stale entry is popped before exhaustion.
        var longLink = new NavLink(0, 0, 0, 0, 2, 0) { Kind = NavLinkKind.Hop };
        var stale = new NavSpace(new[] { Grid(5, 1, (x, _) => x is 0 or 2 or 4) },
            new[] { longLink, longLink with { Kind = NavLinkKind.Stair } });
        yield return (new GridPathPlanner(stale), Start, End(5), Budget(8), NavPathStatus.Partial);
    }
}
