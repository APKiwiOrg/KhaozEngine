using System;
using System.Numerics;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;

namespace KhaozEngine.Tests.Navigation;

// Synthetic engine-owned graphs. No consumer navigation asset is needed for the exact duck request.
internal static class DisconnectedPlannerFixture
{
    internal static NavGrid Grid(int width, int height, Func<int, int, bool> open,
        float y = 0f, float originX = 0f, float originZ = 0f, float cellSize = 1f)
    {
        var blocked = new bool[width * height];
        var heights = new float[blocked.Length];
        for (int z = 0; z < height; z++)
            for (int x = 0; x < width; x++)
            {
                blocked[z * width + x] = !open(x, z);
                heights[z * width + x] = y;
            }
        return NavGrid.FromBlockedSurfaces(width, height, cellSize, originX, originZ,
            blocked, heights, y - 1f, y + 1f);
    }

    internal static NavGoalRegion Member(Vector3 feet)
        => new(feet, 0f, point => point == feet);

    internal static readonly Vector3 DuckStart = new(58.625f, -0.6700001f, -94.625f);
    internal static readonly Vector3 DuckGoal = new(58.67f, -0.67f, -99.42f);

    internal static NavGoalRegion DuckRegion()
        => new(DuckGoal, 0.45000002f, feet => ReachGeometry.Within(
            new MovementBody(feet + new Vector3(0f, 0.25f, 0f), 0.2f, 0.25f), ReachTarget.Point(DuckGoal), 0.25f));

    internal static NavTraversalGraph Graph(NavSpace space, float radius = 0f)
    {
        var layers = new NavTraversalLayer[space.Layers.Count];
        for (int layer = 0; layer < layers.Length; layer++)
        {
            NavGrid grid = space.Layers[layer];
            var accepted = new bool[grid.Width * grid.Height];
            var exits = new byte[accepted.Length];
            for (int z = 0; z < grid.Height; z++)
                for (int x = 0; x < grid.Width; x++)
                {
                    if (!grid.IsPassable(x, z, 0f)) continue;
                    accepted[z * grid.Width + x] = true;
                    for (int i = 0; i < 8; i++)
                        if (grid.IsPassable(x + NavTraversalLayer.NeighborX(i), z + NavTraversalLayer.NeighborZ(i), 0f))
                            exits[z * grid.Width + x] |= (byte)(1 << i);
                }
            layers[layer] = new NavTraversalLayer(grid.Width, grid.Height, accepted, exits);
        }
        return new NavTraversalGraph(space, radius, 0.5f, layers, space.Links);
    }

    internal static NavSpace DuckSpace()
    {
        // The start is the closest end of a broad, disconnected water patch. A separate 3 by 3
        // island supplies the eight exact capsule-reach members around the captured anchor.
        return NavSpace.Single(Grid(260, 986,
            (x, z) => x >= 76 && x <= 199 && z >= 607 && z <= 737 ||
                x >= 75 && x <= 77 && z >= 587 && z <= 589,
            -0.6700001f, 39.5f, -246.5f, 0.25f));
    }
}
