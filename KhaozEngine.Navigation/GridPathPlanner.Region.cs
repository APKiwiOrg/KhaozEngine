using System;
using System.Numerics;

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
            var kernel = CreateRegionSearch(scratch, start, goal, agentRadius, budget, useCertificate);
            return kernel.RunUnbounded();
        }
        finally
        {
            ReturnScratch(scratch);
        }
    }

    SearchKernel<RegionGoal> CreateRegionSearch(SearchScratch scratch, Vector3 start, NavGoalRegion goal,
        float agentRadius, PathQueryBudget budget, bool useCertificate)
        => new(this, scratch, start, goal, new RegionGoal(this, scratch,
            new Vector2(goal.Anchor.X, goal.Anchor.Z), goal.HorizontalExtent), agentRadius, budget, useCertificate);

    void ValidateRegion(NavGoalRegion goal, float agentRadius)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ValidateTraversalRadius(agentRadius);
        if (!_hasRegionSurfaces)
            throw new ArgumentException("Region queries require surface heights on every grid.", nameof(goal));
    }

    bool IsRegionMember(NavGoalRegion goal, float agentRadius, int layer, int x, int z, out Vector3 feet)
    {
        feet = default;
        if (Blocks(layer, agentRadius, x, z)) return false;
        feet = CellFeet(layer, x, z);
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

        public int MemberNode(int index) => scratch.MemberNodeIds[index];

        public Vector3 MemberFeet(int index) => scratch.Members[index];

        public double Priority(int layer, int x, int z)
            => planner._spatialBound.Region(layer, x, z, scratch.MemberBounds, anchorXz, horizontalExtent);

    }

    Vector3 CellFeet(int layer, int x, int z)
    {
        NavGrid grid = _space.Layers[layer];
        Vector2 centre = grid.CellCenter(x, z);
        // Height-less legacy point grids represent a plane. Region queries require physical heights.
        return new Vector3(centre.X, grid.RestingHeightAt(x, z) ?? 0f, centre.Y);
    }

}
