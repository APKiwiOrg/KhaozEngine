using System;

namespace KhaozEngine.Navigation;

/// <summary>The actual float walking prices, widened only after the price has been rounded.</summary>
internal static class PlannerWalkingPrices
{
    static readonly float DiagonalMultiplier = MathF.Sqrt(2f);

    internal static float Cost(float cellSize, bool diagonal)
        => diagonal ? cellSize * DiagonalMultiplier : cellSize;
}
