using System;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>Original operands of an upward world-to-frame support conversion, retained without rounding its error.</summary>
internal readonly record struct SupportHeightTranslation(float LocalHeight, float Origin, float WorldHeight)
{
    internal bool IsValid => float.IsFinite(LocalHeight) && float.IsFinite(Origin) && float.IsFinite(WorldHeight) &&
        OrdinaryMovementEnvironment.ExactSign(LocalHeight, Origin, -WorldHeight) >= 0;
}

/// <summary>Publishes a representable capsule centre whose exact feet cover its geometric support.</summary>
internal static class SupportedCapsulePose
{
    internal static float LocalSupport(float height, float origin)
    {
        float local = height - origin;
        return float.IsFinite(local) && OrdinaryMovementEnvironment.ExactSign(local, origin, -height) < 0
            ? MathF.BitIncrement(local) : local;
    }

    internal static float Centre(float feet, float halfHeight, float placementOffset = 0,
        SupportHeightTranslation translation = default)
    {
        double value = (double)feet + halfHeight + placementOffset;
        float centre = (float)value;
        // A nearest-rounded sum can place exact feet below support. The sign expansion also retains
        // addends too small for binary64, so the upward decision is exact rather than a rounding-error guess.
        if (float.IsFinite(centre) && OrdinaryMovementEnvironment.ExactSign(centre, -halfHeight, -feet) < 0)
            centre = MathF.BitIncrement(centre);
        if (!float.IsFinite(centre) || !translation.IsValid)
            throw new ContactQueryFailure(MovementAvailability.Unresolved);
        // The fixed composition offset is intentional. All numerical displacement, including the
        // world-to-frame conversion, shares one existing precision budget after the final increment.
        Span<float> error = stackalloc float[8]
        {
            centre, -halfHeight, -feet, -placementOffset,
            translation.LocalHeight, translation.Origin, -translation.WorldHeight,
            -MovementQueryLease.CoverageSkinMetres,
        };
        if (OrdinaryMovementEnvironment.ExactSign(error) > 0)
            throw new ContactQueryFailure(MovementAvailability.Unresolved);
        error[7] = MovementQueryLease.CoverageSkinMetres;
        if (OrdinaryMovementEnvironment.ExactSign(error) < 0)
            throw new ContactQueryFailure(MovementAvailability.Unresolved);
        return centre;
    }
}
