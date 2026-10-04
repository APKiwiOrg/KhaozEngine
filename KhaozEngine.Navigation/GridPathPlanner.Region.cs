using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Navigation;

public sealed partial class GridPathPlanner
{
    /// <summary>
    /// Finds a reachable region member through one bounded search over surface-height grids.
    /// Membership uses actual cell-centre feet positions. Returned routes keep every cell edge and
    /// link endpoint without smoothing. Guarded starts must occupy admitted raw-passable own cells.
    /// </summary>
    public NavPath FindPath(Vector3 start, NavGoalRegion goal, float agentRadius, PathQueryBudget budget)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ValidateTraversalRadius(agentRadius);
        for (int layer = 0; layer < _space.Layers.Count; layer++)
            if (!_space.Layers[layer].HasSurfaceHeights)
                throw new ArgumentException("Region queries require surface heights on every grid.", nameof(goal));

        SearchScratch scratch = RentScratch();
        try
        {
            return FindPath(scratch, start, goal, agentRadius, budget);
        }
        finally
        {
            ReturnScratch(scratch);
        }
    }

    NavPath FindPath(SearchScratch scratch, Vector3 start, NavGoalRegion goal, float agentRadius, PathQueryBudget budget)
    {
        int startLayer = _space.LayerAt(start);
        NavGrid startGrid = _space.Layers[startLayer];
        Vector2? startPoint = SnapToPassable(startLayer, startGrid, new Vector2(start.X, start.Z),
            agentRadius, budget.SnapRadius, out _);
        if (startPoint is null) return NavPath.Unreachable;

        if (!FindMembers(scratch, goal, agentRadius)) return NavPath.Unreachable;

        var regionGoal = new RegionGoal(this, scratch, RegionLinksPreserveDistance(),
            new Vector2(goal.Anchor.X, goal.Anchor.Z), goal.HorizontalExtent);
        return RunSearch(scratch, startLayer, startPoint.Value, regionGoal, null, agentRadius, budget);
    }

    /// <summary>
    /// Records the region's passable member cells in the scratch, layer by layer in z then x order. Only
    /// the cells of each layer's <see cref="RegionCellBounds"/> box are visited, and each takes the exact
    /// extent and predicate test, so the members match a whole-grid scan without one. False when no cell is
    /// a member.
    /// </summary>
    bool FindMembers(SearchScratch scratch, NavGoalRegion goal, float agentRadius)
    {
        scratch.ResetMembers();
        int flags = 0;
        for (int layer = 0; layer < _space.Layers.Count; layer++)
        {
            (int x0, int z0, int x1, int z1) = RegionCellBounds(_space.Layers[layer], goal);
            if (x0 <= x1 && z0 <= z1)
                flags = scratch.SetMemberBox(layer, x0, z0, x1 - x0 + 1, z1 - z0 + 1, flags);
        }
        scratch.EnsureMemberFlags(flags);

        for (int layer = 0; layer < _space.Layers.Count; layer++)
        {
            (int x0, int z0, int x1, int z1) = RegionCellBounds(_space.Layers[layer], goal);
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    bool member = IsRegionMember(goal, agentRadius, layer, x, z, out Vector3 feet);
                    scratch.SetMember(layer, x, z, member);
                    if (!member) continue;
                    scratch.MemberLayers[layer] = true;
                    scratch.Members.Add(feet);
                }
        }
        return scratch.Members.Count > 0;
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
    /// A region query's goal: any member cell. Priority is the XZ distance to the extent's edge on a layer
    /// holding members when every link preserves that bound, else zero. Progress is the squared 3D distance
    /// to the nearest member's feet.
    /// </summary>
    readonly struct RegionGoal(GridPathPlanner planner, SearchScratch scratch, bool spatialBound,
        Vector2 anchorXz, float horizontalExtent) : ISearchGoal
    {
        public bool IsGoal(int layer, int x, int z) => scratch.IsMember(layer, x, z);

        public float Priority(int layer, int x, int z)
            => spatialBound && scratch.MemberLayers[layer]
                ? (float)Math.Min(float.MaxValue, Math.Max(0d,
                    RegionDistanceXz(planner._space.Layers[layer].CellCenter(x, z), anchorXz) - horizontalExtent))
                : 0f;

        public double Progress(int layer, int x, int z)
        {
            Vector3 feet = planner.CellFeet(layer, x, z);
            double closest = double.PositiveInfinity;
            foreach (Vector3 member in scratch.Members)
            {
                double dx = (double)feet.X - member.X;
                double dy = (double)feet.Y - member.Y;
                double dz = (double)feet.Z - member.Z;
                closest = Math.Min(closest, dx * dx + dy * dy + dz * dz);
            }
            return closest;
        }
    }

    Vector3 CellFeet(int layer, int x, int z)
    {
        NavGrid grid = _space.Layers[layer];
        Vector2 centre = grid.CellCenter(x, z);
        return new Vector3(centre.X, grid.SurfaceHeightAt(x, z)!.Value, centre.Y);
    }

    static double RegionDistanceXz(Vector2 from, Vector2 to)
    {
        double dx = (double)from.X - to.X;
        double dz = (double)from.Y - to.Y;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    // A cheap same-layer link can undercut XZ distance just like a cross-layer link. Use zero
    // priority for the whole query when any admitted link makes the spatial lower bound uncertain.
    bool RegionLinksPreserveDistance()
    {
        foreach (KeyValuePair<int, List<(int ToId, float CostMeters)>> source in _linkEdges)
        {
            (int layer, int x, int z) = Decode(source.Key);
            Vector2 from = _space.Layers[layer].CellCenter(x, z);
            foreach ((int targetId, float cost) in source.Value)
            {
                (int targetLayer, int tx, int tz) = Decode(targetId);
                if (targetLayer != layer || !float.IsFinite(cost) ||
                    cost < RegionDistanceXz(from, _space.Layers[targetLayer].CellCenter(tx, tz))) return false;
            }
        }
        return true;
    }
}
