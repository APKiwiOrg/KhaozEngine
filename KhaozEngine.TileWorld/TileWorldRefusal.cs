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
}
