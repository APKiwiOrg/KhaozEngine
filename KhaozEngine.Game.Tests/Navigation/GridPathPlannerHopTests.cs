using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Dungeon;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class GridPathPlannerHopTests
{
    const float StepHeight = 0.5f;
    const float AgentHeight = 0f;
    const float JumpHeight = 1.2f;
    const float AgentRadius = 0.2f;
    const float MesaTop = 1.0f;

    // A 3x3 raised mesa (world x, z in [4, 7)) at MesaTop, flat ground elsewhere at height 0. The step bake
    // blocks the mesa rim, leaving only the interior cell (5, 5) passable: an isolated island reachable ONLY
    // by a hop across the blocked rim. Same shape as BakeOverworldHopsTests.
    static bool IsMesa(float x, float z) => x >= 4f && x < 7f && z >= 4f && z < 7f;

    static DelegateSurfaceProvider MesaProvider()
        => new((float x, float z, out float h, out float hr) =>
        {
            hr = float.PositiveInfinity;
            h = IsMesa(x, z) ? MesaTop : 0f;
            return true;
        });

    static NavSpace MesaSpace()
        => NavGridBaker.BakeOverworldHops(
            MesaProvider(),
            minX: 0f, minZ: 0f, maxX: 12f, maxZ: 12f,
            cellSize: 1f, stepHeight: StepHeight, agentHeight: AgentHeight, jumpHeight: JumpHeight);

    // Query endpoints: a ground cell near the low corner, and the isolated mesa-top cell (5, 5). Y is
    // irrelevant on a single-layer space (LayerOf always returns 0), but the goal Y is the mesa top for
    // realism.
    static readonly Vector3 GroundStart = new(1.5f, 0f, 1.5f);
    static readonly Vector3 MesaGoal = new(5.5f, MesaTop, 5.5f);

    [Fact]
    public void Plan_OntoIsolatedTop_CrossesHopLink_LandingMarkedHop()
    {
        var planner = new GridPathPlanner(MesaSpace());

        NavPath path = planner.FindPath(GroundStart, MesaGoal, AgentRadius);

        Assert.Equal(NavPathStatus.Complete, path.Status);

        // Exactly one waypoint is a hop landing, and it is the mesa-top cell (also the goal here).
        int hopCount = path.Waypoints.Count(w => w.Kind == NavWaypointKind.Hop);
        Assert.Equal(1, hopCount);

        int hopIdx = -1;
        for (int i = 0; i < path.Waypoints.Count; i++)
            if (path.Waypoints[i].Kind == NavWaypointKind.Hop) { hopIdx = i; break; }

        // The hop landing is the goal cell center, and its takeoff is the immediately preceding waypoint (a
        // Walk), or the plan origin when the hop is the first segment.
        Assert.Equal(new Vector2(5.5f, 5.5f), path.Waypoints[hopIdx].Position);
        Assert.True(hopIdx == 0 || path.Waypoints[hopIdx - 1].Kind == NavWaypointKind.Walk);
    }

    [Fact]
    public void HopLanding_AndTakeoff_AreNotSmoothedTogether()
    {
        var planner = new GridPathPlanner(MesaSpace());

        NavPath path = planner.FindPath(GroundStart, MesaGoal, AgentRadius);

        Assert.Equal(NavPathStatus.Complete, path.Status);

        int hopIdx = -1;
        for (int i = 0; i < path.Waypoints.Count; i++)
            if (path.Waypoints[i].Kind == NavWaypointKind.Hop) { hopIdx = i; break; }
        Assert.True(hopIdx >= 1, "the takeoff must be its own waypoint, distinct from the landing");

        NavWaypoint takeoff = path.Waypoints[hopIdx - 1];
        NavWaypoint landing = path.Waypoints[hopIdx];
        Assert.Equal(NavWaypointKind.Walk, takeoff.Kind);
        Assert.Equal(NavWaypointKind.Hop, landing.Kind);
        // Distinct positions: the crossing is never collapsed into a single waypoint.
        Assert.NotEqual(takeoff.Position, landing.Position);
    }

    [Fact]
    public void StairSpace_Unchanged()
    {
        DungeonLayout layout = StairLayout();
        NavSpace space = DungeonNav.Bake(layout);
        var planner = new GridPathPlanner(space);

        NavGrid floor0 = space.Layers[0];
        NavGrid floor1 = space.Layers[1];

        (int sx, int sz) = FirstWalkable(layout, 0, fromHighCorner: false);
        (int gx, int gz) = FirstWalkable(layout, 1, fromHighCorner: true);

        Vector2 startXz = floor0.CellCenter(sx, sz);
        Vector2 goalXz = floor1.CellCenter(gx, gz);

        float band0 = layout.FloorHeightMeters * 0.5f;
        float band1 = layout.FloorHeightMeters * 1.5f;
        var start = new Vector3(startXz.X, band0, startXz.Y);
        var goal = new Vector3(goalXz.X, band1, goalXz.Y);

        NavPath path = planner.FindPath(start, goal, AgentRadius);

        Assert.Equal(NavPathStatus.Complete, path.Status);
        // Stair landings stay Walk: no waypoint is marked Hop when the space carries only Stair links.
        Assert.DoesNotContain(path.Waypoints, w => w.Kind == NavWaypointKind.Hop);
    }

    [Fact]
    public void HopCost_HigherThanWalk_PrefersRampWhenCheaper()
    {
        // A single flat layer split by a blocked wall (cx = 3, cz 0..9) with a walk-around gap at the top
        // (cz 10, 11 open). A single Hop link jumps the wall directly at (2, 0) -> (4, 0), Chebyshev 2. The
        // planner may hop the wall or walk the long way round the gap. Which wins is pure cost: a large
        // hopCostCells makes the detour cheaper (no hop), a small one makes the hop cheaper. This is the
        // "detour shorter than the hop wins, longer than the hop loses" behavior in prose.
        NavGrid grid = NavGrid.FromWalkable(7, 12, 1f, 0f, 0f, (cx, cz) => !(cx == 3 && cz <= 9));
        var hop = new NavLink(0, 2, 0, 0, 4, 0) { Kind = NavLinkKind.Hop };
        var space = new NavSpace(new[] { grid }, new[] { hop });

        var start = new Vector3(0.5f, 0f, 0.5f);
        var goal = new Vector3(6.5f, 0f, 0.5f);

        // Large hop cost: the walk-around detour is cheaper, so the plan uses no hop.
        var expensiveHop = new GridPathPlanner(space, hopCostCells: 25f);
        NavPath detourPath = expensiveHop.FindPath(start, goal, AgentRadius);
        Assert.Equal(NavPathStatus.Complete, detourPath.Status);
        Assert.DoesNotContain(detourPath.Waypoints, w => w.Kind == NavWaypointKind.Hop);

        // Small hop cost (still above the 2-cell hop's octile displacement of 2, so admissible): the hop is
        // cheaper than the long detour, so the plan crosses it.
        var cheapHop = new GridPathPlanner(space, hopCostCells: 3f);
        NavPath hopPath = cheapHop.FindPath(start, goal, AgentRadius);
        Assert.Equal(NavPathStatus.Complete, hopPath.Status);
        Assert.Contains(hopPath.Waypoints, w => w.Kind == NavWaypointKind.Hop);

        // The two plans genuinely differ: raising hopCostCells flipped the route off the hop.
        Assert.NotEqual(
            hopPath.Waypoints.Count(w => w.Kind == NavWaypointKind.Hop),
            detourPath.Waypoints.Count(w => w.Kind == NavWaypointKind.Hop));
    }

    [Fact]
    public void Constructor_NonPositiveHopCost_Throws()
    {
        NavSpace space = MesaSpace();
        Assert.Throws<ArgumentOutOfRangeException>(() => new GridPathPlanner(space, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GridPathPlanner(space, -1f));
    }

    [Fact]
    public void Deterministic_HopPlan_SameTwice()
    {
        var planner = new GridPathPlanner(MesaSpace());

        NavPath first = planner.FindPath(GroundStart, MesaGoal, AgentRadius);
        NavPath second = planner.FindPath(GroundStart, MesaGoal, AgentRadius);

        Assert.Equal(first.Status, second.Status);
        // Record-struct equality includes Kind, so this pins the hop marking as well as the geometry.
        Assert.Equal(first.Waypoints, second.Waypoints);
    }

    // First seed in 11..60 whose growth carves at least one stair edge, mirroring DungeonNavTests so the
    // stair-space regression always exercises a real multi-floor layout.
    static DungeonLayout StairLayout()
    {
        var config = new DungeonConfig
        {
            MaxFloors = 3,
            RoomCountTarget = 16,
            LockCount = 0,
            BossRoom = false,
            LoopEdgeBudget = 0,
        };

        for (ulong seed = 11; seed <= 60; seed++)
        {
            DungeonLayout layout = DungeonGenerator.Generate(config, seed);
            if (layout.Edges.Any(e => e.Kind == DungeonEdgeKind.Stair))
                return layout;
        }

        throw new Xunit.Sdk.XunitException("No stair edge was produced across seeds 11..60.");
    }

    static (int X, int Z) FirstWalkable(DungeonLayout layout, int floor, bool fromHighCorner)
    {
        if (fromHighCorner)
        {
            for (int z = layout.Depth - 1; z >= 0; z--)
                for (int x = layout.Width - 1; x >= 0; x--)
                    if (DungeonLayout.IsWalkable(layout.GetCell(x, z, floor)))
                        return (x, z);
        }
        else
        {
            for (int z = 0; z < layout.Depth; z++)
                for (int x = 0; x < layout.Width; x++)
                    if (DungeonLayout.IsWalkable(layout.GetCell(x, z, floor)))
                        return (x, z);
        }

        throw new Xunit.Sdk.XunitException($"No walkable cell found on floor {floor}.");
    }
}
