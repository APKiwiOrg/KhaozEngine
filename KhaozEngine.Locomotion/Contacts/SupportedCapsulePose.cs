using System;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>Publishes a representable capsule centre whose exact feet cover its geometric support.</summary>
internal static class SupportedCapsulePose
{
    internal static float Centre(float feet, float halfHeight, float placementOffset = 0)
    {
        double value = (double)feet + halfHeight + placementOffset;
        float centre = (float)value;
        if (placementOffset != 0 && (!float.IsFinite(centre) ||
                Math.Abs(value - centre) > MovementQueryLease.CoverageSkinMetres))
            throw new ContactQueryFailure(MovementAvailability.Unresolved);
        // A nearest-rounded sum can place exact feet below support. The sign expansion also retains
        // addends too small for binary64, so the upward decision is exact rather than a rounding-error guess.
        return float.IsFinite(centre) && OrdinaryMovementEnvironment.ExactSign(centre, -halfHeight, -feet) < 0
            ? MathF.BitIncrement(centre) : centre;
    }
}
