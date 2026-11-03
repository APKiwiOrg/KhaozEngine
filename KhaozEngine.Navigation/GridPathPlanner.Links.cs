using System;
using System.Numerics;

namespace KhaozEngine.Navigation;

public sealed partial class GridPathPlanner
{
    readonly record struct PlannerLink(int ToId, float CostMeters, NavLinkKind Kind);

    float LinkCost(NavLink link)
    {
        NavGrid from = _space.Layers[link.FromLayer], to = _space.Layers[link.ToLayer];
        if (link.Kind != NavLinkKind.Drop)
            return from.CellSize * (link.Kind == NavLinkKind.Hop ? _hopCostCells : 1f);
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

    bool PointLinksPreserveOctile()
    {
        foreach (var source in _linkEdges)
        {
            var (layer, x, z) = Decode(source.Key);
            foreach (PlannerLink edge in source.Value)
            {
                var (targetLayer, tx, tz) = Decode(edge.ToId);
                if (targetLayer != layer || !float.IsFinite(edge.CostMeters) ||
                    edge.CostMeters < Octile(x, z, tx, tz, _space.Layers[layer].CellSize)) return false;
            }
        }
        return true;
    }

    static NavWaypointKind WaypointKind(NavLinkKind? kind) => kind switch
    {
        NavLinkKind.Hop => NavWaypointKind.Hop,
        NavLinkKind.Drop => NavWaypointKind.Drop,
        _ => NavWaypointKind.Walk,
    };
}
