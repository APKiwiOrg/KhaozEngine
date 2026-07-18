using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Dungeon;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class DungeonNavTests
{
    const float AgentRadius = 0.2f;

    static DungeonConfig FloorsConfig() => new()
    {
        MaxFloors = 3,
        RoomCountTarget = 16,
        LockCount = 0,
        BossRoom = false,
        LoopEdgeBudget = 0,
    };

    // First seed in 11..60 whose growth carves at least one stair edge, so the cross-floor link and
    // end-to-end tests always exercise a real multi-floor layout rather than passing vacuously. Mirrors the
    // fixed-seed idiom in DungeonFloorsTests.
    static DungeonLayout StairLayout()
    {
        for (ulong seed = 11; seed <= 60; seed++)
        {
            DungeonLayout layout = DungeonGenerator.Generate(FloorsConfig(), seed);
            if (layout.Edges.Any(e => e.Kind == DungeonEdgeKind.Stair))
            {
                return layout;
            }
        }

        throw new Xunit.Sdk.XunitException("No stair edge was produced across seeds 11..60.");
    }

    [Fact]
    public void Bake_ProducesOneLayerPerFloor_WithPinnedBandsAndWalkability()
    {
        DungeonLayout layout = StairLayout();
        NavSpace space = DungeonNav.Bake(layout);

        Assert.Equal(layout.Floors, space.Layers.Count);

        for (int f = 0; f < layout.Floors; f++)
        {
            NavGrid grid = space.Layers[f];
            Assert.Equal(layout.Width, grid.Width);
            Assert.Equal(layout.Depth, grid.Height);
            Assert.Equal(layout.CellSizeMeters, grid.CellSize);
            Assert.Equal(f * layout.FloorHeightMeters, grid.YMin);
            Assert.Equal((f + 1) * layout.FloorHeightMeters, grid.YMax);
        }

        // Per-floor walkability matches DungeonLayout.IsWalkable cell for cell. A blocked cell bakes to
        // clearance 0, and a walkable cell always bakes to a positive clearance (the transform seeds every
        // walkable cell at >= 2 half-cells), so clearance == 0 is exactly the not-walkable set.
        NavGrid floor0 = space.Layers[0];
        for (int z = 0; z < layout.Depth; z++)
        {
            for (int x = 0; x < layout.Width; x++)
            {
                bool blocked = floor0.ClearanceAt(x, z) == 0;
                Assert.Equal(!DungeonLayout.IsWalkable(layout.GetCell(x, z, 0)), blocked);
            }
        }
    }

    [Fact]
    public void Bake_LinksJoinStairUpperToStairTop_BothDirections()
    {
        DungeonLayout layout = StairLayout();
        Assert.True(layout.Floors >= 2, "a multi-floor layout is required for cross-floor links");

        NavSpace space = DungeonNav.Bake(layout);
        Assert.NotEmpty(space.Links);

        foreach (NavLink link in space.Links)
        {
            // Every link joins adjacent floors and connects a StairUpper cell (lower floor) to a
            // 4-adjacent StairTop cell one floor up.
            int lower = Math.Min(link.FromLayer, link.ToLayer);
            int upper = Math.Max(link.FromLayer, link.ToLayer);
            Assert.Equal(lower + 1, upper);

            bool fromIsLower = link.FromLayer < link.ToLayer;
            (int upperX, int upperZ) = fromIsLower ? (link.FromX, link.FromZ) : (link.ToX, link.ToZ);
            (int topX, int topZ) = fromIsLower ? (link.ToX, link.ToZ) : (link.FromX, link.FromZ);

            Assert.Equal(DungeonCellKind.StairUpper, layout.GetCell(upperX, upperZ, lower));
            Assert.Equal(DungeonCellKind.StairTop, layout.GetCell(topX, topZ, upper));
            Assert.Equal(1, Math.Abs(topX - upperX) + Math.Abs(topZ - upperZ));
        }

        // Both directions present for every stair: the directed link set is symmetric.
        var all = new HashSet<NavLink>(space.Links);
        foreach (NavLink link in space.Links)
        {
            var reverse = new NavLink(link.ToLayer, link.ToX, link.ToZ, link.FromLayer, link.FromX, link.FromZ);
            Assert.Contains(reverse, all);
        }
    }

    [Fact]
    public void Bake_EndToEnd_PlannerFindsCompleteCrossFloorPath()
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

        // Query Y at each floor's band middle so LayerOf resolves the intended layer unambiguously.
        float band0 = layout.FloorHeightMeters * 0.5f;
        float band1 = layout.FloorHeightMeters * 1.5f;
        var start = new Vector3(startXz.X, band0, startXz.Y);
        var goal = new Vector3(goalXz.X, band1, goalXz.Y);

        NavPath path = planner.FindPath(start, goal, AgentRadius);

        Assert.Equal(NavPathStatus.Complete, path.Status);

        // The path climbs the stair: some consecutive waypoint pair steps from layer 0 to layer 1, the two
        // endpoints of a stair link (always emitted and never smoothed over).
        bool crossed = false;
        for (int i = 0; i + 1 < path.Waypoints.Count; i++)
        {
            if (path.Waypoints[i].Layer == 0 && path.Waypoints[i + 1].Layer == 1)
            {
                crossed = true;
                break;
            }
        }

        Assert.True(crossed, "the path must cross a stair link from floor 0 up to floor 1");
    }

    // First walkable cell scanning from a grid corner: the low corner (z then x ascending) or the high
    // corner (z then x descending). Picks well-separated cross-floor endpoints programmatically.
    static (int X, int Z) FirstWalkable(DungeonLayout layout, int floor, bool fromHighCorner)
    {
        if (fromHighCorner)
        {
            for (int z = layout.Depth - 1; z >= 0; z--)
            {
                for (int x = layout.Width - 1; x >= 0; x--)
                {
                    if (DungeonLayout.IsWalkable(layout.GetCell(x, z, floor)))
                    {
                        return (x, z);
                    }
                }
            }
        }
        else
        {
            for (int z = 0; z < layout.Depth; z++)
            {
                for (int x = 0; x < layout.Width; x++)
                {
                    if (DungeonLayout.IsWalkable(layout.GetCell(x, z, floor)))
                    {
                        return (x, z);
                    }
                }
            }
        }

        throw new Xunit.Sdk.XunitException($"No walkable cell found on floor {floor}.");
    }
}
