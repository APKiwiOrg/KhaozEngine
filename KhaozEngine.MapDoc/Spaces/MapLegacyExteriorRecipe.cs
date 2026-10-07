using System;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>The one eligibility rule for bounded, compatibility-only legacy lower coverage.</summary>
public static class MapLegacyExteriorRecipe
{
    public const string PolicyId = "kemap/legacy-exterior/1";

    public static string? Check(MapSpaceFootprint footprint, MapSpaceDoc space, MapSurfaceRef? lowerSurface)
    {
        ArgumentNullException.ThrowIfNull(footprint);
        ArgumentNullException.ThrowIfNull(space);
        if (footprint.Lower.Kind != MapBoundKind.LegacyExteriorV1 && footprint.Upper.Kind != MapBoundKind.LegacyExteriorV1)
            return null;
        if (space.Kind != MapSpaceKind.Exterior) return "legacy recipe: space";
        if (footprint.Upper.Kind != MapBoundKind.OpenTop) return "legacy recipe: upper";
        if (lowerSurface is null || lowerSurface.Role != MapSurfaceRole.SupportFloor ||
            lowerSurface.PresencePolicy != MapPresencePolicy.LegacyTileWorld ||
            lowerSurface.Frame != MapLatticeFrame.ImportedMetreCentimetre)
            return "legacy recipe: surface";
        if (!string.Equals(footprint.Lattice.SurfaceId, lowerSurface.Id, StringComparison.Ordinal))
            return "legacy recipe: lattice";
        return null;
    }
}
