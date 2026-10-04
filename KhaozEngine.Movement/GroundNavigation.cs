using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

/// <summary>Immutable capsule and area checked ground routes, independent of the capture and physics world.</summary>
public sealed class GroundNavigation
{
    private readonly ProfileTuning _tuning;
    private readonly SwimFractions _swim;
    private readonly NavAreaFootprint _footprint;

    internal GroundNavigation(NavTraversalGraph graph, in MoveTuning tuning, NavAreaFootprint footprint)
        : this(graph, tuning, footprint, aquatic: false)
    {
    }

    internal GroundNavigation(NavTraversalGraph graph, in MoveTuning tuning, NavAreaFootprint footprint, bool aquatic)
    {
        Graph = graph;
        _tuning = new ProfileTuning(tuning.CapsuleRadius, tuning.CapsuleHalfHeight, tuning.MaxSlopeRadians, tuning.StepHeight);
        _swim = new SwimFractions(tuning.SwimEnterDepthFraction, tuning.SwimExitDepthFraction,
            tuning.SwimSurfaceSubmersionFraction);
        _footprint = footprint;
        Aquatic = aquatic;
        Planner = new ProfilePlanner(this, new GridPathPlanner(graph.Space, graph));
    }

    /// <summary>True when the profile was baked aquatic, with float nodes where its body rests while swimming.</summary>
    public bool Aquatic { get; }

    /// <summary>The graph-owned source topology. Candidate Stair links are filtered by the planner's graph.</summary>
    public NavSpace Space => Graph.Space;

    /// <summary>Guarded region and point search with exact radius matching and unsmoothed cell-center routes.
    /// <see cref="RouteApproachOptions.StraightenRoutes"/> opts a <see cref="MoveToRange"/> into straightened
    /// routes.</summary>
    public IRegionPathPlanner Planner { get; }

    /// <summary>The baked capsule radius in metres.</summary>
    public float AgentRadius => Graph.AgentRadius;

    /// <summary>The baked full capsule height in metres.</summary>
    public float AgentHeight => Graph.AgentHeight;

    internal NavTraversalGraph Graph { get; }

    /// <summary>The area footprint over the captured columns, which a bake writes and shares across profiles.</summary>
    internal NavAreaFootprint Footprint => _footprint;

    internal void ValidateTuning(in MoveTuning tuning)
    {
        if (tuning.CapsuleRadius != _tuning.Radius || tuning.CapsuleHalfHeight != _tuning.HalfHeight ||
            tuning.MaxSlopeRadians != _tuning.Slope || tuning.StepHeight != _tuning.Step)
            throw new ArgumentException("Movement geometry must equal the baked profile geometry.", nameof(tuning));
        // The swim fractions place an aquatic profile's float nodes.
        if (Aquatic && (tuning.SwimEnterDepthFraction != _swim.Enter || tuning.SwimExitDepthFraction != _swim.Exit ||
            tuning.SwimSurfaceSubmersionFraction != _swim.Submersion))
            throw new ArgumentException("Swim fractions must equal the baked aquatic profile's.", nameof(tuning));
    }

    /// <summary>Checks captured footprint areas and every directed crossed graph edge.
    /// Unknown, padded, off-grid and incompatible-height endpoints fail. Cross-layer segments must
    /// be one accepted Stair link. This pure guard does not replace live collision resolution.</summary>
    public bool AllowsSegment(Vector3 fromFeet, Vector3 toFeet)
    {
        if (!Resolve(fromFeet, out Node from) || !Resolve(toFeet, out Node to) ||
            !_footprint.AcceptsSegment(fromFeet, toFeet)) return false;
        if (from.Layer != to.Layer)
            return Graph.CanTraverse(from.Layer, from.X, from.Z, to.Layer, to.X, to.Z);
        NavGrid grid = Space.Layers[from.Layer];
        int x = from.X, z = from.Z;
        double dx = (double)toFeet.X - fromFeet.X, dz = (double)toFeet.Z - fromFeet.Z;
        int sx = Math.Sign(dx), sz = Math.Sign(dz);
        double nextX = x == to.X ? double.PositiveInfinity : Crossing(fromFeet.X, dx, grid.OriginX, grid.CellSize, x, sx);
        double nextZ = z == to.Z ? double.PositiveInfinity : Crossing(fromFeet.Z, dz, grid.OriginZ, grid.CellSize, z, sz);
        double strideX = sx == 0 ? double.PositiveInfinity : grid.CellSize / Math.Abs(dx);
        double strideZ = sz == 0 ? double.PositiveInfinity : grid.CellSize / Math.Abs(dz);
        while (x != to.X || z != to.Z)
        {
            int nx = x, nz = z;
            if (nextX <= nextZ) nx += sx;
            if (nextZ <= nextX) nz += sz;
            if (!Graph.CanTraverse(from.Layer, x, z, from.Layer, nx, nz)) return false;
            if (nx != x) nextX = nx == to.X ? double.PositiveInfinity : nextX + strideX;
            if (nz != z) nextZ = nz == to.Z ? double.PositiveInfinity : nextZ + strideZ;
            x = nx;
            z = nz;
        }
        return true;
    }

    private bool Resolve(Vector3 feet, out Node node)
    {
        node = default;
        if (!_footprint.Accepts(feet)) return false;
        double best = double.PositiveInfinity;
        for (int layer = 0; layer < Space.Layers.Count; layer++)
        {
            NavGrid grid = Space.Layers[layer];
            (int x, int z) = grid.CellOf(feet.X, feet.Z);
            float? height = grid.SurfaceHeightAt(x, z);
            if (height is not float y) continue;
            double distance = Math.Abs((double)feet.Y - y);
            if (distance >= best) continue;
            best = distance;
            node = new Node(layer, x, z);
        }
        // Accommodate an in-progress grounded step without accepting an unrelated vertical band.
        return best <= Math.Max(_tuning.Step, GroundTraversalProbe.ArrivalTolerance) &&
            Graph.IsNodePassable(node.Layer, node.X, node.Z);
    }

    private static double Crossing(float start, double delta, float origin, float size, int cell, int direction)
        => direction == 0 ? double.PositiveInfinity :
            (origin + (cell + (direction > 0 ? 1d : 0d)) * size - start) / delta;

    private readonly record struct ProfileTuning(float Radius, float HalfHeight, float Slope, float Step);
    private readonly record struct SwimFractions(float Enter, float Exit, float Submersion);
    private readonly record struct Node(int Layer, int X, int Z);

    private sealed class ProfilePlanner(GroundNavigation navigation, GridPathPlanner planner) : IRegionPathPlanner
    {
        public NavPath FindPath(Vector3 start, Vector3 goal, float agentRadius, PathQueryBudget budget)
        {
            ValidateRadius(agentRadius);
            return navigation.Resolve(start, out _) && navigation.Resolve(goal, out _)
                ? planner.FindPath(start, goal, agentRadius, budget) : NavPath.Unreachable;
        }

        public NavPath FindPath(Vector3 start, NavGoalRegion goal, float agentRadius, PathQueryBudget budget)
        {
            ArgumentNullException.ThrowIfNull(goal);
            ValidateRadius(agentRadius);
            return navigation.Resolve(start, out _)
                ? planner.FindPath(start, goal, agentRadius, budget) : NavPath.Unreachable;
        }

        private void ValidateRadius(float radius)
        {
            if (radius != navigation.AgentRadius)
                throw new ArgumentException("Query radius must equal the baked profile radius.", nameof(radius));
        }
    }
}
