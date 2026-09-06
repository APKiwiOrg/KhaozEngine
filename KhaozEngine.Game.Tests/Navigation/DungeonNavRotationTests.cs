using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Dungeon;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class DungeonNavRotationTests
{
    [Theory]
    [InlineData(MathF.PI / 2)]
    [InlineData(-0.7f)]
    public void Bake_MatchesPlacedGeometryAndRoutesAcrossFloors(float yaw)
    {
        DungeonLayout layout = StairLayout();
        var plot = new DungeonPlotTransform(120, -83, 15, yaw);
        NavSpace space = DungeonNav.Bake(layout, plot);
        for (int f = 0; f < layout.Floors; f++)
        {
            NavGrid grid = space.Layers[f];
            for (int z = 0; z < layout.Depth; z++)
            for (int x = 0; x < layout.Width; x++)
            {
                var expected = plot.TileCenter(new DungeonTile(x, z, f), layout.CellSizeMeters, layout.FloorHeightMeters);
                Vector2 actual = grid.CellCenter(x, z);
                Assert.InRange(Vector2.Distance(new Vector2(expected.X, expected.Z), actual), 0, 0.001f);
                Assert.Equal((x, z), grid.CellOf(expected.X, expected.Z));
                Assert.Equal(DungeonLayout.IsWalkable(layout.GetCell(x, z, f)), grid.ClearanceAt(x, z) > 0);
            }
        }

        DungeonTile first = WalkableCorner(layout, 0, reverse: false);
        DungeonTile last = WalkableCorner(layout, 1, reverse: true);
        var a = plot.TileCenter(first, layout.CellSizeMeters, layout.FloorHeightMeters);
        var b = plot.TileCenter(last, layout.CellSizeMeters, layout.FloorHeightMeters);
        NavPath path = new GridPathPlanner(space).FindPath(
            new Vector3(a.X, a.Y + 0.5f, a.Z), new Vector3(b.X, b.Y + 0.5f, b.Z), 0.2f);
        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.Contains(path.Waypoints, p => p.Layer == 0);
        Assert.Contains(path.Waypoints, p => p.Layer == 1);
        Assert.InRange(Vector2.Distance(new Vector2(b.X, b.Z), path.Waypoints[^1].Position), 0, 0.001f);
    }

    static DungeonTile WalkableCorner(DungeonLayout layout, int floor, bool reverse)
    {
        for (int zi = 0; zi < layout.Depth; zi++)
        for (int xi = 0; xi < layout.Width; xi++)
        {
            int x = reverse ? layout.Width - 1 - xi : xi;
            int z = reverse ? layout.Depth - 1 - zi : zi;
            if (DungeonLayout.IsWalkable(layout.GetCell(x, z, floor))) return new DungeonTile(x, z, floor);
        }
        throw new InvalidOperationException("Fixture floor must have walkable cells.");
    }

    static DungeonLayout StairLayout()
    {
        var config = new DungeonConfig { MaxFloors = 3, RoomCountTarget = 16, LockCount = 0, BossRoom = false, LoopEdgeBudget = 0 };
        for (ulong seed = 11; seed <= 60; seed++)
        {
            DungeonLayout layout = DungeonGenerator.Generate(config, seed);
            if (layout.Edges.Any(e => e.Kind == DungeonEdgeKind.Stair)) return layout;
        }
        throw new InvalidOperationException("Fixture must contain a stair.");
    }
}
