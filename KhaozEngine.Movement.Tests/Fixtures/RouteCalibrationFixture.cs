using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using static KhaozEngine.Tests.Movement.SegmentAdmissionFixture;

namespace KhaozEngine.Tests.Movement;

/// <summary>A frozen query shared by the calibration, work count and publication tests.</summary>
internal sealed record RouteCalibrationFixture(string Name, IIncrementalRegionPathPlanner Planner,
    Vector3 Start, NavGoalRegion Destination, PathQueryBudget Budget, float Radius = 0f)
{
    internal GridPathPlanner.GridSearchRequest Begin(PathPlanWorkspaces pool)
        => (GridPathPlanner.GridSearchRequest)Planner.BeginPath(Start, Destination, Radius, Budget, pool);

    internal PathPlanWorkspaces CreatePool() => Planner.CreateWorkspaces(1, Budget);

    internal static IEnumerable<RouteCalibrationFixture> SearchRows()
    {
        // The reverse link defeats the disconnected certificate. No reachable node is closer than the start.
        var broad = MakeGrid(64, 66, static (_, z) => z != 64);
        yield return Search("neighbors", new([broad], [new NavLink(0, 32, 65, 0, 32, 63)]),
            Feet(32, 63), Goal(Feet(32, 65)), 1024);
        var links = Enumerable.Repeat(new NavLink(0, 0, 0, 0, 2, 0), 2048).ToArray();
        yield return Search("links", new([MakeGrid(3, 1, static (x, _) => x != 1)], links),
            Feet(0, 0), Goal(Feet(2, 0)), 8);

        // 496 connected start-side nodes and a disconnected 16 x 16 member rectangle.
        var certificate = MakeGrid(513, 16, static (x, z) => (x < 496 && z == 0) || x >= 497);
        yield return Search("certificate", NavSpace.Single(certificate), Feet(495, 0),
            new(Feet(504, 7), 8f, static feet => feet.X >= 497.5f), 8);
        yield return Search("membership", NavSpace.Single(MakeGrid(96, 96)), Feet(48, 48),
            new(Feet(48, 48), 96f, static _ => false), 8);
        yield return Search("snap", NavSpace.Single(MakeGrid(63, 63, static (_, _) => false)), Feet(31, 31),
            Goal(Feet(31, 31)), 8, snapRadius: 31);
        yield return Search("long-reconstruction", NavSpace.Single(MakeGrid(2049, 1)), Feet(0, 0),
            Goal(Feet(2048, 0)), 2049);
    }

    internal static IEnumerable<RouteCalibrationFixture> StraighteningRows()
    {
        yield return Straightened("columns", Flat(64, 40, 10f), Feet(16, 20), Feet(34, 20));

        // Narrow vertical and horizontal routes exercise row projection and graph crossing respectively.
        yield return Straightened("rows-mixed", Flat(3, 64, 0f), Feet(1, 2), Feet(1, 60));
        yield return Straightened("crossings-mixed", Flat(64, 3, 0f), Feet(2, 1), Feet(60, 1));

        // Every endpoint visits the entire layer list. Only the base layer has a member at the goal height.
        const int layers = 256;
        var flat = Flat(24, 3, 0f);
        NavGrid upper = NavGrid.FromSurfaces(24, 3, 1f, 0f, 0f,
            static (_, _) => new NavSurfaceSample(true, 4f, float.PositiveInfinity), 0.5f, 1f);
        var multiple = new NavSpace([flat.Space.Layers[0], .. Enumerable.Repeat(upper, layers - 1)]);
        var graph = new NavTraversalGraph(multiple, flat.Graph.AgentRadius, flat.Graph.AgentHeight,
            Enumerable.Repeat(flat.Graph.Layers[0], layers).ToArray(), []);
        var layered = new GroundNavigation(graph, MoveTuning.Default with { CapsuleRadius = 0f }, flat.Footprint);
        yield return Straightened("endpoint-layers", layered, Feet(1, 1), Feet(21, 1));
    }

    internal static IEnumerable<RouteCalibrationFixture> WholeRoutes()
    {
        yield return Walk("small", 12);
        yield return Walk("median", 82);
        yield return Walk("maximum", 153);
        var deadEnd = MakeGrid(4102, 1, static (x, _) => x != 4100);
        yield return Search("unreachable-region-4096", new([deadEnd], [new NavLink(0, 4101, 0, 0, 4099, 0)]),
            Feet(0, 0), Goal(Feet(4101, 0)), 4096);

        // This radius keeps the centre line on one row while the two side guards each span two.
        static RouteCalibrationFixture Walk(string name, int distance)
            => Straightened(name, Flat(distance + 4, 3, 0.01f), Feet(1, 1), Feet(distance + 1, 1));
    }

    static RouteCalibrationFixture Straightened(string name, GroundNavigation navigation, Vector3 start, Vector3 goal)
        => new(name, new RouteStraightener(navigation), start, Goal(goal), PathQueryBudget.Default, navigation.AgentRadius);

    static RouteCalibrationFixture Search(string name, NavSpace space, Vector3 start, NavGoalRegion goal,
        int expansions, float snapRadius = 0f)
        => new(name, new GridPathPlanner(space), start, goal, new() { MaxExpandedNodes = expansions, SnapRadius = snapRadius });

    static NavGrid MakeGrid(int width, int height, Func<int, int, bool>? open = null)
        => NavGrid.FromSurfaces(width, height, 1f, 0f, 0f,
            (x, z) => new NavSurfaceSample(open?.Invoke(x, z) ?? true, 0f, float.PositiveInfinity), 0.5f, 1f);

    static Vector3 Feet(int x, int z) => new(x + 0.5f, 0f, z + 0.5f);
}
