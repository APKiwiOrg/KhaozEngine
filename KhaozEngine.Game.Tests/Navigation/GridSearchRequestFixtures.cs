using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Navigation;
using static KhaozEngine.Tests.Navigation.DisconnectedPlannerFixture;
using static KhaozEngine.Tests.Navigation.GridPathPlannerDisconnectedProgressTests;

namespace KhaozEngine.Tests.Navigation;

/// <summary>Deterministic query fixtures shared by request coverage and the synchronous diagnostic.</summary>
internal static partial class GridSearchRequestFixtures
{
    internal static readonly Vector3 Start = new(0.5f, 0f, 0.5f);

    internal static GridPathPlanner Corridor(int length = 16) => new(CorridorSpace(length));

    static NavSpace CorridorSpace(int length) => NavSpace.Single(Grid(length, 1, (_, _) => true));

    internal static NavGoalRegion End(int length = 16) => Member(new Vector3(length - 0.5f, 0f, 0.5f));

    internal readonly record struct RegionQuery(string Name, GridPathPlanner Planner, Vector3 Start,
        NavGoalRegion Goal, PathQueryBudget Budget, NavPathStatus Status,
        int Searches = 1, int Certificates = 0, int? Waypoints = null, NavSpace? ReferenceSpace = null);

    internal static IEnumerable<RegionQuery> TargetedQueries()
    {
        var corridorSpace = CorridorSpace(16);
        var corridor = new GridPathPlanner(corridorSpace);
        yield return new("cap-1", corridor, Start, End(), Budget(1), NavPathStatus.Unreachable, ReferenceSpace: corridorSpace);
        yield return new("cap-8", corridor, Start, End(), Budget(8), NavPathStatus.Partial, ReferenceSpace: corridorSpace);
        yield return new("empty-region", corridor, Start, new NavGoalRegion(End().Anchor, 1f, _ => false),
            Budget(8), NavPathStatus.Unreachable, Searches: 0, ReferenceSpace: corridorSpace);
        yield return new("snap-refused", corridor, Start with { X = -0.5f }, End(), Budget(8), NavPathStatus.Unreachable,
            Searches: 0, ReferenceSpace: corridorSpace);
        yield return new("snap-accepted", corridor, Start with { X = -0.5f }, End(),
            Budget(64) with { SnapRadius = 2f }, NavPathStatus.Complete, ReferenceSpace: corridorSpace);
        var longCorridor = CorridorSpace(4100);
        yield return new("cap-4096", new GridPathPlanner(longCorridor), Start, End(4100), Budget(4096),
            NavPathStatus.Partial, Waypoints: 4095, ReferenceSpace: longCorridor);

        var upper = Grid(1, 1, (_, _) => true, 4f);
        var lower = Grid(1, 1, (_, _) => true);
        foreach ((string name, NavLinkKind kind) in new[] { ("hop", NavLinkKind.Hop), ("drop", NavLinkKind.Drop) })
        {
            var link = new NavLink(0, 0, 0, 1, 0, 0) { Kind = kind };
            var space = new NavSpace(new[] { upper, lower }, new[] { link });
            var planner = new GridPathPlanner(space);
            // The frozen reference predates Drop, exact incoming edge identity, and the WalkOnly link gate.
            NavSpace? referenceSpace = kind == NavLinkKind.Hop ? space : null;
            yield return new(name, planner, Start with { Y = 4f }, Member(Start), Budget(8),
                NavPathStatus.Complete, ReferenceSpace: referenceSpace);
            yield return new(name + "-walk-only", planner, Start with { Y = 4f }, Member(Start),
                Budget(8) with { WalkOnly = true }, NavPathStatus.Unreachable);
        }

        yield return EqualPriceLinks();

        var yaw = NavGrid.FromSurfaces(8, 3, 1f, 24f, -13f,
            (_, _) => new NavSurfaceSample(true, 0f, 2f), 0.5f, 1f, -1f, 2f, 0.37f);
        Vector2 from = yaw.CellCenter(0, 0), to = yaw.CellCenter(7, 2);
        var yawSpace = NavSpace.Single(yaw);
        yield return new("translated-yawed", new GridPathPlanner(yawSpace), new(from.X, 0f, from.Y),
            Member(new Vector3(to.X, 0f, to.Y)), Budget(64), NavPathStatus.Complete, ReferenceSpace: yawSpace);

        var layeredSpace = new NavSpace(new[] { Grid(8, 1, (_, _) => true), Grid(8, 1, (_, _) => true, 4f) });
        var layered = new GridPathPlanner(layeredSpace);
        var bothLayers = new NavGoalRegion(End(8).Anchor, 0f, feet => feet.X == 7.5f);
        yield return new("two-layers-lower", layered, Start, bothLayers, Budget(64), NavPathStatus.Complete,
            ReferenceSpace: layeredSpace);
        yield return new("two-layers-upper", layered, Start with { Y = 4f }, bothLayers, Budget(64), NavPathStatus.Complete,
            ReferenceSpace: layeredSpace);

        yield return Disconnected("certified-terminal", 32, 2, certified: true);
        yield return Disconnected("certificate-node-cap", 32_769, 1);
        yield return Disconnected("certificate-comparison-cap", 1025, 256);
        yield return StaleEntry();
    }

    internal static IEnumerable<RegionQuery> AdditionalTimingQueries()
    {
        yield return Disconnected("certificate-near-comparison-cap", 1024, 256, certified: true);

        // One weak component via the reverse link. Every reachable node behind the start is farther
        // from the only member. A* reaches its cap without selecting a chain to reconstruct.
        var deadEnd = Grid(1027, 1, (x, _) => x != 1025);
        var space = new NavSpace(new[] { deadEnd }, new[] { new NavLink(0, 1026, 0, 0, 1024, 0) });
        yield return new("bounded-astar-no-reconstruction", new GridPathPlanner(space),
            Start with { X = 1024.5f }, End(1027), Budget(1024), NavPathStatus.Unreachable, Waypoints: 0);

        // The raw region result retains one waypoint per grid exit.
        const int length = 8193;
        yield return new("long-raw-reconstruction", Corridor(length), Start, End(length), Budget(length),
            NavPathStatus.Complete, Waypoints: 8192);
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

    internal static RegionQuery Disconnected(string name, int nodes, int members, bool certified = false)
    {
        NavGrid grid = Grid(nodes + 2, members, (x, z) => x < nodes && z == 0 || x == nodes + 1);
        var goal = new NavGoalRegion(new Vector3(nodes + 1.5f, 0f, 0.5f), members, feet => feet.X == nodes + 1.5f);
        var space = NavSpace.Single(grid);
        return new(name, new GridPathPlanner(space), Start with { X = nodes - 0.5f },
            goal, Budget(8), NavPathStatus.Unreachable, Searches: certified ? 0 : 1,
            Certificates: certified ? 1 : 0, ReferenceSpace: space);
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
