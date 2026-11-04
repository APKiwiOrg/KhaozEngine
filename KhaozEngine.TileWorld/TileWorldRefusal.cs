namespace KhaozEngine.TileWorld;

/// <summary>Stable reasons authored tile-world content or an operation on it is refused.</summary>
public enum TileWorldRefusal
{
    // Region residency.
    /// <summary>Refuses an operation requiring a region that does not exist.</summary>
    RegionMissing,
    /// <summary>Refuses an operation requiring a known region that is not loaded.</summary>
    RegionNotLoaded,
    /// <summary>Refuses unloading a region with unsaved changes.</summary>
    RegionDirty,

    // Manifest regions.
    /// <summary>Refuses a manifest region list that repeats a region coordinate.</summary>
    ManifestRegionDuplicate,
    /// <summary>Refuses a manifest region entry whose hash is null.</summary>
    ManifestRegionHashMissing,
    /// <summary>Refuses region bytes whose hash differs from the manifest's stored hash.</summary>
    RegionHashMismatch,

    // Region content.
    /// <summary>Refuses a region whose plane count differs from the document.</summary>
    RegionPlaneCountMismatch,
    /// <summary>Refuses an object anchored outside its containing region.</summary>
    RegionObjectOutsideBounds,
    /// <summary>Refuses an object on a plane the document does not have.</summary>
    RegionObjectPlaneOutsideRange,
    /// <summary>Refuses a marker located outside its containing region.</summary>
    RegionMarkerOutsideBounds,
    /// <summary>Refuses a marker on a plane the document does not have.</summary>
    RegionMarkerPlaneOutsideRange,
    /// <summary>Refuses a region file whose coordinate differs from its manifest row.</summary>
    RegionCoordinateMismatch,
    /// <summary>Refuses a region file that decodes to no region.</summary>
    RegionFileEmpty,
    /// <summary>Refuses region JSON that cannot be parsed or deserialized.</summary>
    RegionJsonMalformed,

    // Document objects.
    /// <summary>Refuses an operation requiring an object id that is absent.</summary>
    ObjectMissing,
    /// <summary>Refuses insertion of an object id already in use.</summary>
    ObjectIdDuplicate,
    /// <summary>Refuses moving an indexed object absent from its indexed region.</summary>
    ObjectIndexStale,

    // Document markers.
    /// <summary>Refuses an operation requiring a marker name that is absent.</summary>
    MarkerMissing,
    /// <summary>Refuses a marker name already held by an unloaded region.</summary>
    MarkerNameInUnloadedRegion,

    // Dense layer codec.
    /// <summary>Refuses a dense layer with malformed base64 bytes.</summary>
    LayerBase64Malformed,
    /// <summary>Refuses a dense layer whose decoded byte count differs from its shape.</summary>
    LayerByteCountMismatch,

    // Embedded schemas.
    /// <summary>Refuses a catalog schema resource name absent from the assembly.</summary>
    SchemaResourceMissing,

    // World validation.
    /// <summary>Refuses a document with semantic validation issues.</summary>
    WorldValidationFailed,

    // World files and manifest admission.
    /// <summary>Refuses a save when a stale region file cannot be deleted.</summary>
    WorldStaleRegionDeleteFailed,
    /// <summary>Refuses loading a world manifest that cannot be read.</summary>
    ManifestReadFailed,
    /// <summary>Refuses a manifest whose JSON root is not an object.</summary>
    ManifestNotObject,
    /// <summary>Refuses manifest JSON that cannot be parsed or deserialized.</summary>
    ManifestJsonMalformed,
    /// <summary>Refuses a manifest with no format version.</summary>
    ManifestFormatVersionMissing,
    /// <summary>Refuses a manifest whose format version is not an integer.</summary>
    ManifestFormatVersionNotInteger,
    /// <summary>Refuses a manifest newer than the engine's supported format version.</summary>
    ManifestFormatVersionUnsupported,
    /// <summary>Refuses a manifest requiring an unregistered migration step.</summary>
    ManifestMigrationMissing,
    /// <summary>Refuses a manifest migration that returns no JSON object.</summary>
    ManifestMigrationReturnedNull,
    /// <summary>Refuses a manifest that deserializes to no manifest value.</summary>
    ManifestEmpty,
    /// <summary>Refuses a manifest whose plane count is below one.</summary>
    ManifestPlaneCountInvalid,
    /// <summary>Refuses a manifest whose tile size is not positive.</summary>
    ManifestTileSizeInvalid,
    /// <summary>Refuses a manifest with a missing or blank world id.</summary>
    ManifestIdMissing,
    /// <summary>Refuses loading a region file that cannot be read.</summary>
    RegionReadFailed,

    // Prefab shape and file admission.
    /// <summary>Refuses a prefab whose width or height is not positive.</summary>
    PrefabSizeNonPositive,
    /// <summary>Refuses a prefab file whose plane count is not positive.</summary>
    PrefabPlaneCountNonPositive,
    /// <summary>Refuses a prefab whose plane list differs from its declared plane count.</summary>
    PrefabPlaneCountMismatch,
    /// <summary>Refuses a prefab layer whose entry count differs from its declared size.</summary>
    PrefabLayerCountMismatch,
    /// <summary>Refuses a prefab file with an object anchored outside its footprint.</summary>
    PrefabObjectOutsideBounds,
    /// <summary>Refuses a prefab object on a plane the prefab does not have.</summary>
    PrefabObjectPlaneOutsideRange,
    /// <summary>Refuses a prefab file with a marker outside its footprint.</summary>
    PrefabMarkerOutsideBounds,
    /// <summary>Refuses a prefab marker on a plane the prefab does not have.</summary>
    PrefabMarkerPlaneOutsideRange,
    /// <summary>Refuses a prefab file that deserializes to no prefab.</summary>
    PrefabEmpty,
    /// <summary>Refuses prefab JSON that cannot be parsed or deserialized.</summary>
    PrefabJsonMalformed,
    /// <summary>Refuses a prefab file that cannot be read.</summary>
    PrefabReadFailed,

    // Catalog admission and required lookups.
    /// <summary>Refuses catalog JSON that cannot be parsed or deserialized.</summary>
    CatalogJsonMalformed,
    /// <summary>Refuses a catalog that fails its JSON schema.</summary>
    CatalogSchemaMismatch,
    /// <summary>Refuses loading a catalog file that cannot be read.</summary>
    CatalogReadFailed,
    /// <summary>Refuses a material using the id reserved for void.</summary>
    CatalogMaterialIdReserved,
    /// <summary>Refuses a material id already defined by a catalog.</summary>
    CatalogMaterialDuplicate,
    /// <summary>Refuses an archetype whose id is missing or blank.</summary>
    CatalogArchetypeIdMissing,
    /// <summary>Refuses an archetype id already defined by a catalog.</summary>
    CatalogArchetypeDuplicate,
    /// <summary>Refuses a null entry in an archetype's walk surfaces.</summary>
    CatalogWalkSurfaceNull,
    /// <summary>Refuses an archetype collision height that is not finite and positive.</summary>
    CatalogCollisionHeightInvalid,
    /// <summary>Refuses a non-finite walk-surface height or extent.</summary>
    CatalogWalkSurfaceNonFinite,
    /// <summary>Refuses walk-surface bounds whose minimum is not below the maximum.</summary>
    CatalogWalkSurfaceBoundsUnordered,
    /// <summary>Refuses an operation requiring an archetype absent from the catalogs.</summary>
    CatalogArchetypeMissing,

    // Ground material steep companions.
    /// <summary>Refuses a material using itself as its steep companion.</summary>
    SteepSelfReference,
    /// <summary>Refuses a steep companion absent from the same catalog.</summary>
    SteepCompanionMissing,
    /// <summary>Refuses a water material as a steep companion.</summary>
    SteepCompanionWater,
    /// <summary>Refuses a steep blend start outside the open right angle.</summary>
    SteepFromOutsideRightAngle,
    /// <summary>Refuses a steep blend end outside the open right angle.</summary>
    SteepToOutsideRightAngle,
    /// <summary>Refuses a steep blend whose resolved start is not below its end.</summary>
    SteepBandUnordered,
    /// <summary>Refuses a steep blend whose single-precision endpoint cosines cannot be ordered.</summary>
    SteepCosinesCollapsed,
}
