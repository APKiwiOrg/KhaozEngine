using System;
using System.Numerics;

namespace KhaozEngine.Navigation;

public sealed partial class GridPathPlanner
{
    readonly record struct PlannerLink(int ToId, float CostMeters, NavLinkKind Kind);

    // Narrow observations for the #1712 cost-policy contract. Neither changes planner state.
    internal double SpatialBoundScaleForTests => _spatialBound.Scale;
    internal float LinkCostMetersForTests(NavLink link) => LinkCost(link);

    float LinkCost(NavLink link) => LinkCost(link,
        _spatialBound.Distance(link.FromLayer, link.FromX, link.FromZ, link.ToLayer, link.ToX, link.ToZ));

    float LinkCost(NavLink link, double spatialSpan)
    {
        NavGrid from = _space.Layers[link.FromLayer], to = _space.Layers[link.ToLayer];
        if (link.Kind == NavLinkKind.Hop) return from.CellSize * _hopCostCells;
        if (link.Kind != NavLinkKind.Drop)
        {
            double minimum = Math.Max(from.CellSize, spatialSpan);
            if (!double.IsFinite(minimum) || minimum > float.MaxValue)
                throw new ArgumentException("Stair cost must be representable.", nameof(link));
            float rounded = (float)minimum;
            // Rounding down would discount the metric and weaken the construction-time bound.
            return rounded < minimum ? MathF.BitIncrement(rounded) : rounded;
        }
        float sourceY = from.RestingHeightAt(link.FromX, link.FromZ) ??
            throw new ArgumentException("Drop endpoints require physical heights.", nameof(link));
        float targetY = to.RestingHeightAt(link.ToX, link.ToZ) ??
            throw new ArgumentException("Drop endpoints require physical heights.", nameof(link));
        double descent = (double)sourceY - targetY;
        if (descent <= 0) throw new ArgumentException("Drop links must descend.", nameof(link));
        Vector2 a = from.CellCenter(link.FromX, link.FromZ), b = to.CellCenter(link.ToX, link.ToZ);
        double dx = Math.Abs((double)a.X - b.X), dz = Math.Abs((double)a.Y - b.Y);
        double cost = Math.Max(dx, dz) + (Sqrt2 - 1d) * Math.Min(dx, dz) + descent;
        if (!double.IsFinite(cost) || cost > float.MaxValue)
            throw new ArgumentException("Drop cost must be representable.", nameof(link));
        return (float)cost;
    }

    static NavWaypointKind WaypointKind(NavLinkKind? kind) => kind switch
    {
        NavLinkKind.Hop => NavWaypointKind.Hop,
        NavLinkKind.Drop => NavWaypointKind.Drop,
        _ => NavWaypointKind.Walk,
    };
}
