using System;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>The physical resting offset and footprint shared by one leased movement operation.</summary>
internal readonly record struct ContactComposition
{
    internal float PlacementOffset { get; }
    internal GroundCoreSettings Foot { get; }

    internal ContactComposition(float placementOffset, GroundCoreSettings foot)
    {
        if (placementOffset != 0f && placementOffset != LeasedContactQueryContext.PlacementSkin)
            throw new ArgumentOutOfRangeException(nameof(placementOffset));
        PlacementOffset = placementOffset;
        Foot = foot;
    }

    internal static ContactComposition Ordinary(in MoveTuning tuning) =>
        new(0f, new(tuning.FootRadiusFraction));

    internal static ContactComposition PublicExplicit(in MoveTuning tuning) =>
        new(LeasedContactQueryContext.PlacementSkin, new(tuning.FootRadiusFraction));

    internal float RestingFeet(float supportHeight) => PlacementOffset == 0f
        ? supportHeight : GroundPlacement.NotBelow(RestingFeet((double)supportHeight));

    // Adapter centres retain their double sum until the existing checked float mapping.
    internal double RestingFeet(double supportHeight) => supportHeight + PlacementOffset;
}
