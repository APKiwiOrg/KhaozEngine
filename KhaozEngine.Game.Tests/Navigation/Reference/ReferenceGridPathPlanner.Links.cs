// Frozen allocating reference after the #1712 Stair cost ruling for 23.3.0.
// Keep independent of production scratch. Change only with an evidenced reference update.
using System;
using KhaozEngine.Navigation;

namespace KhaozEngine.Tests.Navigation;

internal sealed partial class ReferenceGridPathPlanner
{
    float LinkCost(NavLink link, double span)
    {
        float cell = _space.Layers[link.FromLayer].CellSize;
        if (link.Kind != NavLinkKind.Stair)
            return cell * (link.Kind == NavLinkKind.Hop ? _hopCostCells : 1f);
        double minimum = Math.Max(cell, span);
        if (!double.IsFinite(minimum) || minimum > float.MaxValue)
            throw new ArgumentException("Stair cost must be representable.", nameof(link));
        float cost = (float)minimum;
        return cost < minimum ? MathF.BitIncrement(cost) : cost;
    }
}
