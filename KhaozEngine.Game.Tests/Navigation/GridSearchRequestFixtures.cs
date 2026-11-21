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

    internal readonly record struct RegionQuery(string Name, GridPathPlanner Planner, Vector3 Start,
        NavGoalRegion Goal, PathQueryBudget Budget, NavPathStatus Status);

    internal static IEnumerable<RegionQuery> TargetedQueries()
    {
        var corridor = Corridor();
        yield return new("cap-1", corridor, Start, End(), Budget(1), NavPathStatus.Unreachable);
        yield return new("cap-8", corridor, Start, End(), Budget(8), NavPathStatus.Partial);
        yield return new("empty-region", corridor, Start, new NavGoalRegion(End().Anchor, 1f, _ => false),
            Budget(8), NavPathStatus.Unreachable);
        yield return new("snap-refused", corridor, Start with { X = -0.5f }, End(), Budget(8), NavPathStatus.Unreachable);
        yield return new("snap-accepted", corridor, Start with { X = -0.5f }, End(),
            Budget(64) with { SnapRadius = 2f }, NavPathStatus.Complete);
        yield return new("cap-4096", Corridor(4100), Start, End(4100), Budget(4096), NavPathStatus.Partial);

        var upper = Grid(1, 1, (_, _) => true, 4f);
        var lower = Grid(1, 1, (_, _) => true);
        foreach ((string name, NavLinkKind kind) in new[] { ("hop", NavLinkKind.Hop), ("drop", NavLinkKind.Drop) })
        {
            var link = new NavLink(0, 0, 0, 1, 0, 0) { Kind = kind };
            var planner = new GridPathPlanner(new NavSpace(new[] { upper, lower }, new[] { link }));
            yield return new(name, planner, Start with { Y = 4f }, Member(Start), Budget(8), NavPathStatus.Complete);
            yield return new(name + "-walk-only", planner, Start with { Y = 4f }, Member(Start),
                Budget(8) with { WalkOnly = true }, NavPathStatus.Unreachable);
        }

        yield return EqualPriceLinks();

        var yaw = NavGrid.FromSurfaces(8, 3, 1f, 24f, -13f,
            (_, _) => new NavSurfaceSample(true, 0f, 2f), 0.5f, 1f, -1f, 2f, 0.37f);
        Vector2 from = yaw.CellCenter(0, 0), to = yaw.CellCenter(7, 2);
        yield return new("translated-yawed", new GridPathPlanner(NavSpace.Single(yaw)), new(from.X, 0f, from.Y),
            Member(new Vector3(to.X, 0f, to.Y)), Budget(64), NavPathStatus.Complete);

        var layered = new GridPathPlanner(new NavSpace(new[] { Grid(8, 1, (_, _) => true), Grid(8, 1, (_, _) => true, 4f) }));
        var bothLayers = new NavGoalRegion(End(8).Anchor, 0f, feet => feet.X == 7.5f);
        yield return new("two-layers-lower", layered, Start, bothLayers, Budget(64), NavPathStatus.Complete);
        yield return new("two-layers-upper", layered, Start with { Y = 4f }, bothLayers, Budget(64), NavPathStatus.Complete);

        yield return Disconnected("certified-terminal", 32, 2);
        yield return Disconnected("certificate-node-cap", 32_769, 1);
        yield return Disconnected("certificate-comparison-cap", 1025, 256);
        yield return StaleEntry();
    }

    internal static RegionQuery EqualPriceLinks()
    {
        // Equal-price links into the same node retain the selected incoming edge.
        var stair = new NavLink(0, 0, 0, 1, 0, 0);
        var space = new NavSpace(new[] { Grid(1, 1, (_, _) => true, 4f), Grid(1, 1, (_, _) => true) },
            new[] { stair with { Kind = NavLinkKind.Hop }, stair });
        return new("equal-price-links", new GridPathPlanner(space, hopCostCells: 1f), Start with { Y = 4f },
            Member(Start), Budget(8), NavPathStatus.Complete);
    }

    internal static RegionQuery Disconnected(string name, int nodes, int members)
    {
        NavGrid grid = Grid(nodes + 2, members, (x, z) => x < nodes && z == 0 || x == nodes + 1);
        var goal = new NavGoalRegion(new Vector3(nodes + 1.5f, 0f, 0.5f), members, feet => feet.X == nodes + 1.5f);
        return new(name, new GridPathPlanner(NavSpace.Single(grid)), Start with { X = nodes - 0.5f },
            goal, Budget(8), NavPathStatus.Unreachable);
    }

    internal static RegionQuery StaleEntry()
    {
        // An open entry for node 2 is improved by a cheaper parallel edge. The goal is outside
        // that component, so the stale entry is popped before exhaustion.
        var longLink = new NavLink(0, 0, 0, 0, 2, 0) { Kind = NavLinkKind.Hop };
        var stale = new NavSpace(new[] { Grid(5, 1, (x, _) => x is 0 or 2 or 4) },
            new[] { longLink, longLink with { Kind = NavLinkKind.Stair } });
        return new("stale-entry", new GridPathPlanner(stale), Start, End(5), Budget(8), NavPathStatus.Partial);
    }
}
