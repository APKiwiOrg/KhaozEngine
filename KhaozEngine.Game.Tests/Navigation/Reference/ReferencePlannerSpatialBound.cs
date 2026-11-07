// Frozen allocating reference after the #1712 F1 metric correction for 23.3.0.
// Keep independent of production scratch. Change only with an evidenced reference update.
using System;
using System.Collections.Generic;
using System.Numerics;

using KhaozEngine.Navigation;

namespace KhaozEngine.Tests.Navigation;

/// <summary>A construction-time cost per metre lower bound in one metric shared by every layer.</summary>
internal sealed class ReferencePlannerSpatialBound
{
    static readonly float Sqrt2 = MathF.Sqrt(2f);
    readonly IReadOnlyList<NavGrid> _layers;
    readonly bool _sharedFrame;

    internal float Scale { get; }

    internal ReferencePlannerSpatialBound(NavSpace space, NavTraversalGraph? traversal, Func<NavLink, double, float> linkCost)
    {
        _layers = space.Layers;
        _sharedFrame = true;
        NavGrid first = _layers[0];
        foreach (NavGrid grid in _layers)
            _sharedFrame &= grid.OriginX == first.OriginX && grid.OriginZ == first.OriginZ &&
                grid.CellSize == first.CellSize && grid.YawRadians == first.YawRadians;

        double scale = 1d;
        // In a shared frame the walking span is exactly its existing orthogonal/diagonal cost.
        // Otherwise use actual world centres, including float rounding in translated/yawed grids.
        if (!_sharedFrame)
            for (int layer = 0; layer < _layers.Count; layer++)
                scale = Math.Min(scale, WalkingScale(layer, traversal));
        foreach (NavLink link in traversal?.Links ?? space.Links)
        {
            double span = Distance(link.FromLayer, link.FromX, link.FromZ, link.ToLayer, link.ToX, link.ToZ);
            float cost = linkCost(link, span);
            scale = Constrain(scale, cost, span);
        }
        // Round downward so storing the ratio cannot raise the promised lower bound.
        float rounded = (float)scale;
        Scale = rounded > scale ? MathF.BitDecrement(rounded) : rounded;
    }

    internal float Point(int layer, int x, int z, int goalLayer, int gx, int gz)
        => Scale == 0f ? 0f : (float)(Scale * Distance(layer, x, z, goalLayer, gx, gz));

    internal double Distance(int layer, int x, int z, int goalLayer, int gx, int gz)
    {
        if (_sharedFrame)
        {
            int dx = Math.Abs(x - gx), dz = Math.Abs(z - gz);
            // Preserve the historical float octile expression exactly when the ratio is one.
            return _layers[layer].CellSize * (Sqrt2 * Math.Min(dx, dz) + Math.Abs(dx - dz));
        }
        return WorldDistance(_layers[layer].CellCenter(x, z), _layers[goalLayer].CellCenter(gx, gz));
    }

    internal float Region(int layer, int x, int z, ReferencePlannerRegionBounds members, Vector2 anchor, float extent)
    {
        if (Scale == 0f) return 0f;
        double span;
        if (_sharedFrame)
        {
            // Membership uses rounded world feet. Its actual cell indices still share the cost metric.
            (int gx, int gz) = members.Closest(x, z);
            span = Distance(layer, x, z, layer, gx, gz);
        }
        else
        {
            Vector2 centre = _layers[layer].CellCenter(x, z);
            double dx = Math.Max(Math.Abs((double)centre.X - anchor.X) - extent, 0d);
            double dz = Math.Max(Math.Abs((double)centre.Y - anchor.Y) - extent, 0d);
            span = Math.Sqrt(dx * dx + dz * dz);
        }
        return (float)Math.Min(float.MaxValue, Scale * span);
    }

    double WalkingScale(int layer, NavTraversalGraph? traversal)
    {
        NavGrid grid = _layers[layer];
        double scale = 1d;
        for (int z = 0; z < grid.Height; z++)
            for (int x = 0; x < grid.Width; x++)
            {
                if (!grid.IsPassable(x, z, 0f)) continue;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int nx = x + dx, nz = z + dz;
                        if (!grid.IsPassable(nx, nz, 0f) ||
                            traversal is not null && !traversal.CanWalkNeighbor(layer, x, z, nx, nz)) continue;
                        bool diagonal = dx != 0 && dz != 0;
                        if (diagonal && (!grid.IsPassable(nx, z, 0f) || !grid.IsPassable(x, nz, 0f))) continue;
                        scale = Constrain(scale, diagonal ? grid.CellSize * Sqrt2 : grid.CellSize,
                            WorldDistance(grid.CellCenter(x, z), grid.CellCenter(nx, nz)));
                    }
            }
        return scale;
    }

    static double Constrain(double scale, float cost, double span)
    {
        if (!float.IsFinite(cost) || !double.IsFinite(span)) return 0d;
        if (span == 0d) return scale; // A purely vertical link imposes no spatial constraint.
        return cost <= 0f ? 0d : Math.Min(scale, cost / span);
    }

    static double WorldDistance(Vector2 from, Vector2 to)
    {
        double dx = (double)from.X - to.X, dz = (double)from.Y - to.Y;
        return Math.Sqrt(dx * dx + dz * dz);
    }
}
