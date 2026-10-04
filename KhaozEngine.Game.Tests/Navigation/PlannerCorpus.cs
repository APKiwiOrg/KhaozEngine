using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Navigation;

namespace KhaozEngine.Tests.Navigation;

/// <summary>
/// Seeded generator for planner equality facts: obstacle grids with optional surface heights, yaw and
/// several layers joined by stair and hop links, guarded traversal graphs over them, and query endpoints,
/// regions and budgets drawn from the same seed. Every value derives from the seed, so a failing case
/// replays exactly.
/// </summary>
internal sealed class PlannerCorpus
{
    const float LayerSpacing = 3f;

    readonly Random _rng;

    public PlannerCorpus(int seed) => _rng = new Random(seed);

    public NavSpace Space { get; private set; } = null!;

    public float CellSize { get; private set; }

    /// <summary>Builds a fresh space: one to three layers of 12 to 40 cells a side, a quarter blocked.</summary>
    public NavSpace NextSpace(bool surfaces, int maxLayers = 3)
    {
        int layerCount = surfaces ? _rng.Next(1, maxLayers + 1) : 1;
        int width = _rng.Next(12, 41);
        int height = _rng.Next(12, 41);
        CellSize = new[] { 0.25f, 0.5f, 1f }[_rng.Next(3)];
        float originX = (float)(_rng.NextDouble() * 20 - 10);
        float originZ = (float)(_rng.NextDouble() * 20 - 10);
        float yaw = _rng.Next(3) == 0 ? (float)(_rng.NextDouble() * Math.PI * 2) : 0f;
        double density = 0.1 + _rng.NextDouble() * 0.25;

        var layers = new NavGrid[layerCount];
        for (int layer = 0; layer < layerCount; layer++)
        {
            bool[] open = new bool[width * height];
            float[] lift = new float[width * height];
            for (int i = 0; i < open.Length; i++)
            {
                open[i] = _rng.NextDouble() >= density;
                lift[i] = _rng.Next(4) == 0 ? 0.25f : 0f;
            }
            float baseY = layer * LayerSpacing;
            layers[layer] = surfaces
                ? NavGrid.FromSurfaces(width, height, CellSize, originX, originZ,
                    (x, z) => new NavSurfaceSample(open[z * width + x], baseY + lift[z * width + x], 2f),
                    0.5f, 1f, baseY - 1f, baseY + 2f, yaw)
                : NavGrid.FromWalkable(width, height, CellSize, originX, originZ,
                    (x, z) => open[z * width + x], yawRadians: yaw);
        }

        var links = new List<NavLink>();
        for (int layer = 0; layer + 1 < layerCount; layer++)
        {
            for (int i = 0; i < 4; i++)
            {
                int x = _rng.Next(width);
                int z = _rng.Next(height);
                links.Add(new NavLink(layer, x, z, layer + 1, x, z));
                links.Add(new NavLink(layer + 1, x, z, layer, x, z));
            }
        }
        for (int i = 0; i < _rng.Next(0, 5); i++)
        {
            int layer = _rng.Next(layerCount);
            int x = _rng.Next(width - 2);
            int z = _rng.Next(height);
            links.Add(new NavLink(layer, x, z, layer, x + 2, z) { Kind = NavLinkKind.Hop });
            if (_rng.Next(2) == 0)
                links.Add(new NavLink(layer, x + 2, z, layer, x, z) { Kind = NavLinkKind.Hop });
        }

        Space = new NavSpace(layers, links);
        return Space;
    }

    /// <summary>A guarded graph at radius zero accepting most raw-passable cells, most exits and links.</summary>
    public NavTraversalGraph NextGraph()
    {
        var layers = new NavTraversalLayer[Space.Layers.Count];
        for (int layer = 0; layer < layers.Length; layer++)
        {
            NavGrid grid = Space.Layers[layer];
            var accepted = new bool[grid.Width * grid.Height];
            for (int z = 0; z < grid.Height; z++)
                for (int x = 0; x < grid.Width; x++)
                    accepted[z * grid.Width + x] = grid.IsPassable(x, z, 0f) && _rng.NextDouble() < 0.92;

            var exits = new byte[accepted.Length];
            int[] dx = { 1, -1, 0, 0, 1, 1, -1, -1 };
            int[] dz = { 0, 0, 1, -1, 1, -1, 1, -1 };
            for (int z = 0; z < grid.Height; z++)
                for (int x = 0; x < grid.Width; x++)
                {
                    if (!accepted[z * grid.Width + x]) continue;
                    for (int i = 0; i < 8; i++)
                    {
                        int nx = x + dx[i], nz = z + dz[i];
                        if (nx < 0 || nz < 0 || nx >= grid.Width || nz >= grid.Height) continue;
                        if (accepted[nz * grid.Width + nx] && _rng.NextDouble() < 0.9)
                            exits[z * grid.Width + x] |= (byte)(1 << i);
                    }
                }
            layers[layer] = new NavTraversalLayer(grid.Width, grid.Height, accepted, exits);
        }

        var links = new List<NavLink>();
        foreach (NavLink link in Space.Links)
            if (layers[link.FromLayer].IsAccepted(link.FromX, link.FromZ) &&
                layers[link.ToLayer].IsAccepted(link.ToX, link.ToZ) && _rng.NextDouble() < 0.8)
                links.Add(link);
        return new NavTraversalGraph(Space, 0f, 1f, layers, links);
    }

    /// <summary>A world point near a random cell of a random layer, sometimes just outside the grid.</summary>
    public Vector3 NextPoint()
    {
        int layer = _rng.Next(Space.Layers.Count);
        NavGrid grid = Space.Layers[layer];
        int x = _rng.Next(-1, grid.Width + 1);
        int z = _rng.Next(-1, grid.Height + 1);
        Vector2 centre = grid.CellCenter(x, z);
        float jitterX = (float)(_rng.NextDouble() - 0.5) * CellSize;
        float jitterZ = (float)(_rng.NextDouble() - 0.5) * CellSize;
        return new Vector3(centre.X + jitterX, layer * LayerSpacing + 0.1f, centre.Y + jitterZ);
    }

    /// <summary>A region around a random point: a disc, a ring, a one-cell target or an empty predicate.</summary>
    public NavGoalRegion NextRegion()
    {
        Vector3 anchor = NextPoint();
        float extent = new[] { 0f, 0.5f, 1f, 2.5f, 4f, 9f }[_rng.Next(6)] * CellSize * 2f;
        int shape = _rng.Next(4);
        float inner = extent * 0.5f;
        return new NavGoalRegion(anchor, extent, feet =>
        {
            float dx = feet.X - anchor.X, dz = feet.Z - anchor.Z;
            float distance = MathF.Sqrt(dx * dx + dz * dz);
            bool level = MathF.Abs(feet.Y - anchor.Y) <= 1.5f;
            return shape switch
            {
                0 => level && distance <= extent,
                1 => level && distance <= extent && distance >= inner,
                2 => Math.Abs(dx) <= extent && Math.Abs(dz) <= extent,
                _ => false,
            };
        });
    }

    public PathQueryBudget NextBudget() => _rng.Next(5) switch
    {
        0 => new PathQueryBudget { MaxExpandedNodes = 30, SnapRadius = 1f },
        1 => new PathQueryBudget { MaxExpandedNodes = 250, SnapRadius = 0f },
        2 => new PathQueryBudget { MaxExpandedNodes = 0, SnapRadius = 3f },
        _ => PathQueryBudget.Default,
    };

    public float NextRadius() => new[] { 0f, 0f, 0.5f, 1f, 1.5f }[_rng.Next(5)] * CellSize;

    public bool NextBool() => _rng.Next(2) == 0;
}
