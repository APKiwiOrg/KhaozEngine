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
        foreach (NavGrid grid in _space.Layers)
            if (!grid.HasSurfaceHeights)
                throw new ArgumentException("Region queries require surface heights on every grid.", nameof(goal));

        int startLayer = _space.LayerAt(start);
        NavGrid startGrid = _space.Layers[startLayer];
        Vector2? startPoint = SnapToPassable(startLayer, startGrid, new Vector2(start.X, start.Z),
            agentRadius, budget.SnapRadius, out _);
        if (startPoint is null) return NavPath.Unreachable;

        var isMember = new bool[_totalNodes];
        var memberLayers = new bool[_space.Layers.Count];
        var members = new List<Vector3>();
        for (int layer = 0; layer < _space.Layers.Count; layer++)
        {
            NavGrid grid = _space.Layers[layer];
            for (int z = 0; z < grid.Height; z++)
                for (int x = 0; x < grid.Width; x++)
                {
                    if (Blocks(layer, agentRadius, x, z)) continue;
                    Vector3 feet = CellFeet(layer, x, z);
                    if (Math.Abs((double)feet.X - goal.Anchor.X) > goal.HorizontalExtent ||
                        Math.Abs((double)feet.Z - goal.Anchor.Z) > goal.HorizontalExtent) continue;
                    if (!goal.Contains(feet)) continue;
                    isMember[_layerOffset[layer] + z * grid.Width + x] = true;
                    memberLayers[layer] = true;
                    members.Add(feet);
                }
        }
        if (members.Count == 0) return NavPath.Unreachable;

        bool spatialBound = RegionLinksPreserveDistance();
        var anchorXz = new Vector2(goal.Anchor.X, goal.Anchor.Z);
        float Priority(int layer, int x, int z)
            => spatialBound && memberLayers[layer]
                ? (float)Math.Min(float.MaxValue, Math.Max(0d,
                    RegionDistanceXz(_space.Layers[layer].CellCenter(x, z), anchorXz) - goal.HorizontalExtent))
                : 0f;
        double Progress(int layer, int x, int z)
        {
            Vector3 feet = CellFeet(layer, x, z);
            double closest = double.PositiveInfinity;
            foreach (Vector3 member in members)
            {
                double dx = (double)feet.X - member.X;
                double dy = (double)feet.Y - member.Y;
                double dz = (double)feet.Z - member.Z;
                closest = Math.Min(closest, dx * dx + dy * dy + dz * dz);
            }
            return closest;
        }

        return RunSearch(startLayer, startPoint.Value,
            (layer, x, z) => isMember[_layerOffset[layer] + z * _space.Layers[layer].Width + x],
            Priority, Progress, null, agentRadius, budget);
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
