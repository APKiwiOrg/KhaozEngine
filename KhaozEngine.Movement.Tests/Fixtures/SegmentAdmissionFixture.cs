using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;

namespace KhaozEngine.Tests.Movement;

/// <summary>Captured flat columns with explicit graph exits independent of the footprint guard.</summary>
internal static class SegmentAdmissionFixture
{
    internal static GroundNavigation Flat(int width, int height, float radius,
        Func<int, int, uint>? areas = null, Func<int, int, int, int, bool>? allowsEdge = null)
    {
        var options = new PhysicsNavBakeOptions(0, 0, width, height, 1, 8, 9, 0.8f, width * height, width * height);
        int count = width * height;
        var starts = new int[count + 1];
        var surfaces = new PhysicsNavSurface[count];
        for (int i = 0; i < count; i++)
        {
            starts[i] = i;
            surfaces[i] = new(0, float.PositiveInfinity, areas?.Invoke(i % width, i / width) ?? 0u);
        }
        starts[^1] = count;
        var columns = PhysicsNavColumns.Own(options, width, height, starts, surfaces, []);
        var tuning = MoveTuning.Default with { CapsuleRadius = radius };
        return Build(new NavAreaFootprint(columns, options, tuning, default), tuning, allowsEdge: allowsEdge);
    }

    internal static GroundNavigation Build(NavAreaFootprint footprint, MoveTuning tuning, float upper = float.NaN,
        NavLink[]? links = null, Func<int, int, int, int, bool>? allowsEdge = null)
    {
        int width = footprint.Columns.Width, height = footprint.Columns.Height;
        PhysicsNavBakeOptions options = footprint.Options;
        NavGrid Layer(float y) => NavGrid.FromSurfaces(width, height, options.CellSize, options.MinX, options.MinZ,
            (_, _) => new NavSurfaceSample(true, y, float.PositiveInfinity), stepHeight: 0.4f, agentHeight: 1.5f);
        NavGrid[] grids = float.IsNaN(upper) ? [Layer(0f)] : [Layer(0f), Layer(upper)];
        var accepted = new bool[width * height];
        Array.Fill(accepted, true);
        var exits = new byte[accepted.Length];
        (int X, int Z)[] offsets = [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)];
        for (int z = 0; z < height; z++)
            for (int x = 0; x < width; x++)
                for (int i = 0; i < offsets.Length; i++)
                {
                    int nx = x + offsets[i].X, nz = z + offsets[i].Z;
                    if ((uint)nx < (uint)width && (uint)nz < (uint)height && (allowsEdge?.Invoke(x, z, nx, nz) ?? true))
                        exits[z * width + x] |= (byte)(1 << i);
                }
        var layer = new NavTraversalLayer(width, height, accepted, exits);
        var space = new NavSpace(grids, links);
        var graph = new NavTraversalGraph(space, tuning.CapsuleRadius, 2f * tuning.CapsuleHalfHeight,
            Enumerable.Repeat(layer, grids.Length).ToArray(), links ?? []);
        return new GroundNavigation(graph, tuning, footprint);
    }

    internal static NavGoalRegion Goal(Vector3 feet) => new(feet, 0f, candidate => candidate == feet);

    /// <summary>A fixed raw path keeps the straightening timing window independent of A*.</summary>
    internal sealed class FixedPath(NavPath path) : IRegionPathPlanner
    {
        public NavPath FindPath(Vector3 start, Vector3 goal, float agentRadius, PathQueryBudget budget) => path;

        public NavPath FindPath(Vector3 start, NavGoalRegion goal, float agentRadius, PathQueryBudget budget) => path;
    }
}
