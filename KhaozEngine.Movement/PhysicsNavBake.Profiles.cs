using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

public sealed partial class PhysicsNavBake
{
    private static readonly (int X, int Z)[] ProfileNeighbors =
        [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)];

    /// <summary>Bakes one capsule class against the captured area tags and unchanged static physics.
    /// The slope must equal the capture slope. Every admitted hold, directed neighbor and Stair link
    /// is proved through the movement core with dry medium and unit pace. The result owns pure data.</summary>
    public GroundNavigation BuildProfile(in MoveTuning tuning, NavAreaFilter areas)
    {
        GroundMoveContext context = Context;
        context.ValidateTuning(tuning);
        if (tuning.MaxSlopeRadians != Options.MaxSlopeRadians)
            throw new ArgumentException("Profile slope must equal the capture slope.", nameof(tuning));
        float height = 2f * tuning.CapsuleHalfHeight;
        if (!float.IsFinite(height))
            throw new ArgumentOutOfRangeException(nameof(tuning), "Profile height must be finite.");
        var footprint = new NavAreaFootprint(Columns, Options, tuning, areas);
        var candidates = new CandidateColumns(this, context, tuning, footprint);
        NavSpace space = NavLayerBaker.BakeGroundedLayered(candidates,
            Options.MinX, Options.MinZ, Options.MaxX, Options.MaxZ, Options.CellSize,
            tuning.StepHeight, height, Options.MaxSurfacesPerColumn, maxLayerCells: Options.MaxLayerCells);

        var nodes = new bool[space.Layers.Count][];
        var exits = new byte[space.Layers.Count][];
        for (int layer = 0; layer < space.Layers.Count; layer++)
        {
            NavGrid grid = space.Layers[layer];
            int count = checked(grid.Width * grid.Height);
            nodes[layer] = new bool[count];
            exits[layer] = new byte[count];
            for (int z = 0; z < grid.Height; z++)
                for (int x = 0; x < grid.Width; x++)
                    nodes[layer][z * grid.Width + x] = grid.IsPassable(x, z, 0f);
        }

        var layers = new NavTraversalLayer[space.Layers.Count];
        for (int layer = 0; layer < space.Layers.Count; layer++)
        {
            NavGrid grid = space.Layers[layer];
            for (int z = 0; z < grid.Height; z++)
                for (int x = 0; x < grid.Width; x++)
                {
                    if (!nodes[layer][z * grid.Width + x]) continue;
                    for (int direction = 0; direction < ProfileNeighbors.Length; direction++)
                    {
                        int nx = x + ProfileNeighbors[direction].X, nz = z + ProfileNeighbors[direction].Z;
                        if (!grid.InBounds(nx, nz) || !nodes[layer][nz * grid.Width + nx]) continue;
                        if (Prove(context, tuning, Feet(grid, x, z), Feet(grid, nx, nz), footprint))
                            exits[layer][z * grid.Width + x] |= (byte)(1 << direction);
                    }
                }
            layers[layer] = new NavTraversalLayer(grid.Width, grid.Height, nodes[layer], exits[layer]);
        }
        var links = new List<NavLink>();
        foreach (NavLink link in space.Links)
        {
            if (link.Kind != NavLinkKind.Stair) continue;
            if (!layers[link.FromLayer].IsAccepted(link.FromX, link.FromZ) ||
                !layers[link.ToLayer].IsAccepted(link.ToX, link.ToZ)) continue;
            if (Prove(context, tuning, Feet(space.Layers[link.FromLayer], link.FromX, link.FromZ),
                Feet(space.Layers[link.ToLayer], link.ToX, link.ToZ), footprint)) links.Add(link);
        }
        _ = Context;
        var graph = new NavTraversalGraph(space, tuning.CapsuleRadius, height, layers, links);
        return new GroundNavigation(graph, tuning, footprint);
    }

    private bool Prove(GroundMoveContext context, in MoveTuning tuning, Vector3 from, Vector3 to, NavAreaFootprint footprint)
    {
        _ = Context;
        bool accepted = GroundTraversalProbe.TryEdge(context, tuning, from, to, footprint.AcceptsPredicate,
            Options.EdgeProbeSeconds, Options.MaxEdgeProbeSteps);
        _ = Context;
        return accepted;
    }

    private static Vector3 Feet(NavGrid grid, int x, int z)
    {
        Vector2 center = grid.CellCenter(x, z);
        return new Vector3(center.X, grid.SurfaceHeightAt(x, z)!.Value, center.Y);
    }

    private sealed class CandidateColumns(PhysicsNavBake bake, GroundMoveContext context,
        MoveTuning tuning, NavAreaFootprint footprint) : INavColumnProvider
    {
        public int SampleColumn(float x, float z, Span<NavSurfaceSample> surfaces)
        {
            int count = bake.Columns.SampleColumn(x, z, surfaces), accepted = 0;
            for (int i = 0; i < count; i++)
            {
                NavSurfaceSample surface = surfaces[i];
                if (surface.Headroom < 2f * tuning.CapsuleHalfHeight) continue;
                Vector3 feet = new(x, surface.Height, z);
                if (bake.Prove(context, tuning, feet, feet, footprint)) surfaces[accepted++] = surface;
            }
            return accepted;
        }
    }
}
