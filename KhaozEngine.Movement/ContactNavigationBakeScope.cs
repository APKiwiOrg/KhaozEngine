using System;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

/// <summary>Every navigation proof leases under the bake's water boundary scope, so a bake cannot forget it. A proof
/// built from a runtime binding could take the capture certificate where the full coverage solve refuses, and publish
/// a link, a Drop or a whole profile the bake refuses. Each proof constructor refuses any other source. See
/// <see cref="MovementWaterBoundaryScope.NavigationBake"/>.</summary>
internal static class ContactNavigationBakeScope
{
    internal static EffectiveMovementBinding Require(EffectiveMovementBinding source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.WaterBoundaryScope != MovementWaterBoundaryScope.NavigationBake)
            throw new InvalidOperationException(
                "A navigation proof requires a navigation bake binding, whose leases take the full water coverage solve.");
        return source;
    }
}
