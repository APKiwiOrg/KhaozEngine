using System;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>The one eligibility rule for bounded, compatibility-only legacy lower coverage.</summary>
public static class MapLegacyExteriorRecipe
{
    public const string PolicyId = "kemap/legacy-exterior/1";

    public static string? Check(MapSpaceFootprint footprint, MapSpaceDoc space, MapSurfaceRef? lowerSurface) =>
        CheckFinding(footprint, space, lowerSurface)?.Message;

    /// <summary>Returns the first failed compatibility eligibility rule and its typed subjects.</summary>
    public static MapDocumentFinding? CheckFinding(MapSpaceFootprint footprint, MapSpaceDoc space, MapSurfaceRef? lowerSurface)
    {
        ArgumentNullException.ThrowIfNull(footprint);
        ArgumentNullException.ThrowIfNull(space);
        if (footprint.Lower.Kind != MapBoundKind.LegacyExteriorV1 && footprint.Upper.Kind != MapBoundKind.LegacyExteriorV1)
            return null;
        if (space.Kind != MapSpaceKind.Exterior) return Finding(MapDocumentRefusal.LegacyExteriorSpaceRequired, "legacy recipe: space", "Space.Kind");
        if (footprint.Upper.Kind != MapBoundKind.OpenTop) return Finding(MapDocumentRefusal.LegacyExteriorOpenTopRequired, "legacy recipe: upper", "Upper.Kind");
        if (lowerSurface is null || lowerSurface.Role != MapSurfaceRole.SupportFloor ||
            lowerSurface.PresencePolicy != MapPresencePolicy.LegacyTileWorld ||
            lowerSurface.Frame != MapLatticeFrame.ImportedMetreCentimetre)
            return Finding(lowerSurface is null ? MapDocumentRefusal.SchemaValueRequired : MapDocumentRefusal.SurfaceLegacyRecipeRequired,
                "legacy recipe: surface", lowerSurface is null ? "Lower.Surface" : lowerSurface.Role != MapSurfaceRole.SupportFloor
                    ? "Lower.Surface.Role" : lowerSurface.PresencePolicy != MapPresencePolicy.LegacyTileWorld
                    ? "Lower.Surface.PresencePolicy" : "Lower.Surface.Frame");
        if (!string.Equals(footprint.Lattice.SurfaceId, lowerSurface.Id, StringComparison.Ordinal))
            return Finding(MapDocumentRefusal.AuthoredOwnershipMismatch, "legacy recipe: lattice", "Lattice.SurfaceId");
        return null;

        MapDocumentFinding Finding(MapDocumentRefusal reason, string message, string field) => new(reason, message)
        {
            RecordId = footprint.Id,
            SpaceId = space.Id,
            SurfaceId = lowerSurface?.Id ?? footprint.Lower.SurfaceId,
            Patch = footprint.Lattice,
            FieldPath = field,
        };
    }
}
