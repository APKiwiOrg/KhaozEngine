using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

/// <summary>Immutable capsule and area checked ground routes, independent of the capture and physics world.</summary>
public sealed partial class GroundNavigation
{
    private readonly ProfileTuning _tuning;
    private readonly SwimFractions _swim;
    private readonly NavAreaFootprint _footprint;

    internal GroundNavigation(NavTraversalGraph graph, in MoveTuning tuning, NavAreaFootprint footprint)
        : this(graph, tuning, footprint, aquatic: false)
    {
    }

    internal GroundNavigation(NavTraversalGraph graph, in MoveTuning tuning, NavAreaFootprint footprint, bool aquatic, float maxDropMetres = 0,
        bool allowTerrainDescents = false)
    {
        Graph = graph;
        _tuning = new ProfileTuning(tuning.CapsuleRadius, tuning.CapsuleHalfHeight, tuning.MaxSlopeRadians, tuning.StepHeight);
        _swim = new SwimFractions(tuning.SwimEnterDepthFraction, tuning.SwimExitDepthFraction,
            tuning.SwimSurfaceSubmersionFraction);
        _footprint = footprint;
        Aquatic = aquatic;
        MaxDropMetres = maxDropMetres == 0 ? 0 : maxDropMetres;
        AllowTerrainDescents = allowTerrainDescents;
        Identity = new(new NavBakeProfile("", tuning, footprint.Areas)
        {
            Aquatic = aquatic,
            MaxDropMetres = MaxDropMetres,
            AllowTerrainDescents = allowTerrainDescents
        }.ResolveKind(), tuning, MaxDropMetres, allowTerrainDescents);
        Planner = new ProfilePlanner(this, new GridPathPlanner(graph.Space, graph));
    }

    /// <summary>True when the profile was baked aquatic, with float nodes where its body rests while swimming.</summary>
    public bool Aquatic { get; }

    /// <summary>The baked unpowered descent permission in metres. Zero disables Drop topology.
    /// This is a profile permission, independent of manual falling and fall damage.</summary>
    public float MaxDropMetres { get; }

    /// <summary>Whether this profile owns certified continuous-terrain Drop transitions.</summary>
    public bool AllowTerrainDescents { get; }

    /// <summary>The graph-owned source topology. Candidate links are filtered by the planner's graph.</summary>
    public NavSpace Space => Graph.Space;

    /// <summary>Guarded region and point search with exact radius matching and unsmoothed cell-center routes.
    /// <see cref="RouteApproachOptions.StraightenRoutes"/> opts a <see cref="MoveToRange"/> into straightened
    /// routes. Direct callers first call <see cref="Validate(EffectiveMovementBinding)"/> with their runtime binding.</summary>
    public IRegionPathPlanner Planner { get; }

    /// <summary>The baked capsule radius in metres.</summary>
    public float AgentRadius => Graph.AgentRadius;

    /// <summary>The baked full capsule height in metres.</summary>
    public float AgentHeight => Graph.AgentHeight;

    internal NavTraversalGraph Graph { get; }

    internal NavProfileIdentity Identity { get; }

    /// <summary>The area footprint over the captured columns, which a bake writes and shares across profiles.</summary>
    internal NavAreaFootprint Footprint => _footprint;

    /// <summary>Checks captured footprint areas and every directed crossed graph edge.
    /// Unknown, padded, off-grid and incompatible-height endpoints fail. Cross-layer segments must
    /// be one accepted Stair link. This pure guard does not replace live collision resolution. Direct callers first
    /// call <see cref="Validate(EffectiveMovementBinding)"/> with their runtime binding.</summary>
    public bool AllowsSegment(Vector3 fromFeet, Vector3 toFeet)
    {
        if (!Resolve(fromFeet, out Node from) || !Resolve(toFeet, out Node to) ||
            !_footprint.AcceptsSegment(fromFeet, toFeet)) return false;
        if (ChangesLayer(from.Layer, to.Layer))
            return Graph.CanWalk(from.Layer, from.X, from.Z, to.Layer, to.X, to.Z);
        NavGrid grid = Space.Layers[from.Layer];
        int x = from.X, z = from.Z;
        double dx = CrossingDelta(fromFeet.X, toFeet.X), dz = CrossingDelta(fromFeet.Z, toFeet.Z);
        int sx = Math.Sign(dx), sz = Math.Sign(dz);
        double nextX = FirstCrossing(x, to.X, fromFeet.X, dx, grid, sx, alongZ: false);
        double nextZ = FirstCrossing(z, to.Z, fromFeet.Z, dz, grid, sz, alongZ: true);
        double strideX = CrossingStride(grid, dx, sx);
        double strideZ = CrossingStride(grid, dz, sz);
        while (x != to.X || z != to.Z)
        {
            NextCrossing(x, z, sx, sz, nextX, nextZ, out int nx, out int nz);
            if (!Graph.CanWalk(from.Layer, x, z, from.Layer, nx, nz)) return false;
            if (!SameCell(nx, x)) nextX = AfterCrossing(nx, to.X, nextX, strideX);
            if (!SameCell(nz, z)) nextZ = AfterCrossing(nz, to.Z, nextZ, strideZ);
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
            ProbeEndpointLayer(feet, layer, ref best, ref node);
        return AcceptsEndpoint(best, node);
    }

    private readonly record struct ProfileTuning(float Radius, float HalfHeight, float Slope, float Step);
    private readonly record struct SwimFractions(float Enter, float Exit, float Submersion);

    private sealed partial class ProfilePlanner(GroundNavigation navigation, GridPathPlanner planner) : IIncrementalRegionPathPlanner
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
