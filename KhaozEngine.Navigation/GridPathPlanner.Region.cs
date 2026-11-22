using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Navigation;

public sealed partial class GridPathPlanner
{
    /// <summary>
    /// Finds a route to an admitted region member over surface-height grids. A certified disconnected
    /// terminal skips the bounded search.
    /// Membership uses actual cell-centre feet positions. Returned routes keep every cell edge and
    /// link endpoint without smoothing. Guarded starts must occupy admitted raw-passable own cells.
    /// </summary>
    public NavPath FindPath(Vector3 start, NavGoalRegion goal, float agentRadius, PathQueryBudget budget)
        => FindPathCore(start, goal, agentRadius, budget, useCertificate: true);

    internal NavPath FindPathWithoutCertificateForTests(Vector3 start, NavGoalRegion goal, float agentRadius, PathQueryBudget budget)
        => FindPathCore(start, goal, agentRadius, budget, useCertificate: false);

    NavPath FindPathCore(Vector3 start, NavGoalRegion goal, float agentRadius, PathQueryBudget budget, bool useCertificate)
    {
        ValidateRegion(goal, agentRadius);
        SearchScratch scratch = RentScratch();
        try
        {
            return FindPath(scratch, start, goal, agentRadius, budget, useCertificate);
        }
        finally
        {
            ReturnScratch(scratch);
        }
    }

    NavPath FindPath(SearchScratch scratch, Vector3 start, NavGoalRegion goal, float agentRadius,
        PathQueryBudget budget, bool useCertificate)
    {
        int startLayer = _space.LayerAt(start);
        NavGrid startGrid = _space.Layers[startLayer];
        Vector2? startPoint = SnapToPassable(startLayer, startGrid, new Vector2(start.X, start.Z),
            agentRadius, budget.SnapRadius, out _);
        if (startPoint is null) return NavPath.Unreachable;

        if (!FindMembers(scratch, goal, agentRadius)) return NavPath.Unreachable;

        return RunSearch(scratch, startLayer, startPoint.Value, CreateRegionGoal(scratch, goal),
            null, agentRadius, budget, useCertificate);
    }

    RegionGoal CreateRegionGoal(SearchScratch scratch, NavGoalRegion goal)
        => new(this, scratch, new Vector2(goal.Anchor.X, goal.Anchor.Z), goal.HorizontalExtent);

    void ValidateRegion(NavGoalRegion goal, float agentRadius)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ValidateTraversalRadius(agentRadius);
        if (!_hasRegionSurfaces)
            throw new ArgumentException("Region queries require surface heights on every grid.", nameof(goal));
    }

    /// <summary>Records members in layer, z, x order using each layer's padded candidate box.</summary>
    bool FindMembers(SearchScratch scratch, NavGoalRegion goal, float agentRadius)
    {
        scratch.ResetMembers();
        bool[] candidates = scratch.SynchronousCandidateLayers;
        int flags = 0;
        for (int layer = 0; layer < _space.Layers.Count; layer++)
        {
            (int x0, int z0, int x1, int z1) = RegionCellBounds(_space.Layers[layer], goal);
            flags = scratch.SetMemberBox(layer, x0, z0, x1 - x0 + 1, z1 - z0 + 1, flags);
            candidates[layer] = x0 <= x1 && z0 <= z1;
        }
        scratch.EnsureMemberFlags(flags);

        for (int layer = 0; layer < _space.Layers.Count; layer++)
        {
            if (!candidates[layer]) continue;
            NavGrid grid = _space.Layers[layer];
            int layerBase = _layerOffset[layer];
            SearchScratch.MemberBox box = scratch.CandidateBox(layer);
            int x1 = box.X0 + box.Width - 1, z1 = box.Z0 + box.Height - 1;
            for (int z = box.Z0; z <= z1; z++)
                for (int x = box.X0; x <= x1; x++)
                    TestRegionMember(scratch, goal, agentRadius, grid, layer, layerBase, x, z);
        }
        return scratch.Members.Count > 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void TestRegionMember(SearchScratch scratch, NavGoalRegion goal, float agentRadius,
        NavGrid grid, int layer, int layerBase, int x, int z)
    {
        bool member = IsRegionMember(goal, agentRadius, grid, layer, x, z, out Vector3 feet);
        scratch.SetMember(layer, x, z, member);
        if (member) scratch.AddMember(feet, x, z, layerBase + z * grid.Width + x);
    }

    bool IsRegionMember(NavGoalRegion goal, float agentRadius, NavGrid grid, int layer, int x, int z, out Vector3 feet)
    {
        feet = default;
        if (Blocks(grid, layer, agentRadius, x, z)) return false;
        feet = CellFeet(grid, x, z);
        if (Math.Abs((double)feet.X - goal.Anchor.X) > goal.HorizontalExtent ||
            Math.Abs((double)feet.Z - goal.Anchor.Z) > goal.HorizontalExtent) return false;
        return goal.Contains(feet);
    }

    /// <summary>
    /// The inclusive cell box of <paramref name="grid"/> holding every cell whose centre can lie within the
    /// region's horizontal extent square: the square mapped into the grid's local frame (its yawed bounding
    /// box), padded one cell per side for rounding and clamped to the grid. An empty box has
    /// <c>X0 &gt; X1</c> or <c>Z0 &gt; Z1</c>.
    /// </summary>
    static (int X0, int Z0, int X1, int Z1) RegionCellBounds(NavGrid grid, NavGoalRegion goal)
    {
        double cos = Math.Cos(grid.YawRadians);
        double sin = Math.Sin(grid.YawRadians);
        double dx = (double)goal.Anchor.X - grid.OriginX;
        double dz = (double)goal.Anchor.Z - grid.OriginZ;
        double localX = dx * cos + dz * sin;
        double localZ = -dx * sin + dz * cos;
        double reach = goal.HorizontalExtent * (Math.Abs(cos) + Math.Abs(sin));
        (int x0, int x1) = CellSpan(localX, reach, grid.CellSize, grid.Width);
        (int z0, int z1) = CellSpan(localZ, reach, grid.CellSize, grid.Height);
        return (x0, z0, x1, z1);
    }

    static (int Low, int High) CellSpan(double centre, double reach, float cellSize, int cells)
    {
        // Cell c's centre sits at (c + 0.5) * cellSize on this local axis.
        double low = Math.Max(0d, Math.Floor((centre - reach) / cellSize - 0.5) - 1d);
        double high = Math.Min(cells - 1d, Math.Ceiling((centre + reach) / cellSize - 0.5) + 1d);
        return low > high ? (1, 0) : ((int)low, (int)high);
    }

    /// <summary>
    /// A region query's goal: any member cell. Priority uses the cached metric and scale on every layer.
    /// Shared frames bound the actual members' cell box, so rounded world centres cannot change the metric.
    /// Progress is the squared 3D distance to the nearest member's feet.
    /// </summary>
    readonly struct RegionGoal(GridPathPlanner planner, SearchScratch scratch,
        Vector2 anchorXz, float horizontalExtent) : ISearchGoal
    {
        public bool IsGoal(int layer, int x, int z) => scratch.IsMember(layer, x, z);
        public int MemberCount => scratch.Members.Count;

        bool ISearchGoal.IsDisconnected(PlannerConnectivity connectivity, int component)
        {
            foreach (int node in scratch.MemberNodeIds)
                if (!CertificateMemberIsDisconnected(connectivity, component, node)) return false;
            return true;
        }

        public double Priority(int layer, int x, int z)
            => planner._spatialBound.Region(layer, x, z, scratch.MemberBounds, anchorXz, horizontalExtent);

        double ISearchGoal.Progress(int layer, int x, int z)
        {
            Vector3 feet = planner.CellFeet(layer, x, z);
            double closest = double.PositiveInfinity;
            foreach (Vector3 member in scratch.Members)
                closest = ReduceProgress(closest, feet, member);
            return closest;
        }
    }

    Vector3 CellFeet(int layer, int x, int z) => CellFeet(_space.Layers[layer], x, z);

    static Vector3 CellFeet(NavGrid grid, int x, int z)
    {
        Vector2 centre = grid.CellCenter(x, z);
        // Height-less legacy point grids represent a plane. Region queries require physical heights.
        return new Vector3(centre.X, grid.RestingHeightAt(x, z) ?? 0f, centre.Y);
    }

}
