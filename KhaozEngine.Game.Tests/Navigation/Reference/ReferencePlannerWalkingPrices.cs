// Frozen allocating reference after the #1712 F2 double-cost ruling for 23.3.0.
// Keep independent of production scratch. Change only with an evidenced reference update.
using System;

using KhaozEngine.Navigation;

namespace KhaozEngine.Tests.Navigation;

/// <summary>The actual float walking prices, widened only after the price has been rounded.</summary>
internal static class ReferencePlannerWalkingPrices
{
    static readonly float DiagonalMultiplier = MathF.Sqrt(2f);

    internal static float Cost(float cellSize, bool diagonal)
        => diagonal ? cellSize * DiagonalMultiplier : cellSize;
}
