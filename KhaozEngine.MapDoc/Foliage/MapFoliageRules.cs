using KhaozEngine.MapDoc.Prefabs;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>One row of a layer's compatibility ordinal table. It relates a canonical support surface to the source plane
/// ordinal it derives from. Surface IDs are unique and case-sensitive within a layer, and different surfaces may share
/// an ordinal. An empty table means every owner falls back to canonical surface identity.</summary>
public sealed record MapFoliageCompatibilityOrdinal(string SurfaceId, int Ordinal);

/// <summary>A finite XZ rectangle with positive area, in metres.</summary>
public readonly record struct MapFoliageRect(float MinX, float MinZ, float MaxX, float MaxZ);

public enum MapFoliageClearanceKind { Solid, Door, RoofAbove }

/// <summary>A clearance rule keyed by asset ID and an optional placement tag. Its footprint is in the owner's local frame
/// and is applied through the effective transform of every matching placement. Rules carry no ordering member.</summary>
public sealed record MapFoliageClearanceRule(string Id, string AssetId, string? PlacementTag, MapFoliageClearanceKind Kind,
    MapFoliageRect Footprint);

/// <summary>The seated support of an explicit-height owner, a World Placement, Surface, Record or Marker.</summary>
public sealed record MapFoliageOwnerSupport(MapPrefabReference Owner, MapSupportBinding Support);

/// <summary>An exceptional owner-bound clearing mask. Support names the clearing target, never the owner's seat, and the
/// footprint moves with the owner. Masks carry no ordering member.</summary>
public sealed record MapFoliageMask(string Id, MapPrefabReference Owner, MapSupportBinding Support, MapFoliageClearanceKind Kind,
    MapFoliageRect Footprint);
