using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using static KhaozEngine.Tests.Movement.SegmentAdmissionFixture;

namespace KhaozEngine.Tests.Movement;

/// <summary>Fixed synchronous workloads portable to a build without route requests.</summary>
internal readonly record struct StraighteningTimingRow(string Name, Func<int> Run, bool Large)
{
    internal static IEnumerable<StraighteningTimingRow> Create()
    {
        foreach (var (name, navigation, from, to, large) in new[]
        {
            ("small", Flat(9, 6, 0.25f), new Vector3(1.5f, 0f, 2.5f), new Vector3(6.5f, 0f, 2.5f), false),
            ("medium", Flat(48, 20, 4f), new Vector3(8.5f, 0f, 8.5f), new Vector3(39.5f, 0f, 8.5f), true),
            ("large", Flat(192, 72, 24f), new Vector3(32.5f, 0f, 32.5f), new Vector3(159.5f, 0f, 32.5f), true),
            ("side-refusal", Flat(9, 6, 0.25f, (x, z) => x == 3 && z == 3 ? 1u : 0u).ExcludingAreas(1u),
                new Vector3(1.5f, 0f, 2.5f), new Vector3(6.5f, 0f, 2.5f), false),
        })
        {
            yield return new($"segment-{name}", () => navigation.AllowsSegment(from, to) ? 1 : 0, large);
            yield return Route($"straighten-{name}", navigation, from, to,
                [Waypoint((from + to) * 0.5f), Waypoint(to)], large);
        }

        GroundNavigation flat = Flat(9, 6, 0.25f);
        Vector3 start = new(1.5f, 0f, 2.5f), end = new(6.5f, 0f, 2.5f);
        yield return new("segment-refused-endpoint", () => flat.AllowsSegment(default, end) ? 1 : 0, false);
        yield return Route("straighten-unreachable", flat, start, end, [], false, NavPathStatus.Unreachable);
        yield return Route("straighten-single", flat, start, end, [Waypoint(end)], false);
        yield return Route("straighten-malformed", flat, start, end,
            [Waypoint(new Vector3(3.5f, 0f, 2.5f)), Waypoint(end, layer: 3)], false);
        yield return Route("straighten-special", flat, start, end,
            [Waypoint(new Vector3(3.5f, 0f, 2.5f)), Waypoint(end) with { Kind = NavWaypointKind.Hop }], false);

        var tuning = MoveTuning.Default with { CapsuleRadius = 0f };
        var columns = NavAreaFootprintRowsTests.Columns(NavAreaFootprintRowsTests.Options());
        var footprint = new NavAreaFootprint(columns, columns.Options, tuning, default, surfaceReach: 2d);
        GroundNavigation layers = Build(footprint, tuning, 4f, [new NavLink(0, 2, 2, 1, 3, 2)]);
        Vector3 stairFrom = new(2.5f, 0f, 2.5f), stairTo = new(3.5f, 4f, 2.5f);
        yield return new("segment-stair", () => layers.AllowsSegment(stairFrom, stairTo) ? 1 : 0, false);
        yield return Route("straighten-layer", layers, stairFrom, stairTo,
            [Waypoint(stairFrom), Waypoint(stairTo, layer: 1)], false);
    }

    static StraighteningTimingRow Route(string name, GroundNavigation navigation, Vector3 start, Vector3 goalFeet,
        NavWaypoint[] waypoints, bool large, NavPathStatus status = NavPathStatus.Complete)
    {
        var raw = new NavPath(status, waypoints);
        var planner = new RouteStraightener(new FixedPath(raw), navigation.Space, navigation.AllowsSegment);
        NavGoalRegion goal = Goal(goalFeet);
        return new(name, () => planner.FindPath(start, goal, navigation.AgentRadius, PathQueryBudget.Default).Waypoints.Count, large);
    }

    static NavWaypoint Waypoint(Vector3 feet, int layer = 0) => new(new Vector2(feet.X, feet.Z), layer);
}
