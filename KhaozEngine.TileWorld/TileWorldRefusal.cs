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

    // PGM heightmap input.
    /// <summary>Refuses a PGM file that cannot be read from the supplied path.</summary>
    PgmReadFailed,
    /// <summary>Refuses an image without the binary PGM P5 signature.</summary>
    PgmMagicUnsupported,
    /// <summary>Refuses a PGM with a non-positive dimension.</summary>
    PgmDimensionsNonPositive,
    /// <summary>Refuses a PGM maxval outside one through 65535.</summary>
    PgmMaxValueOutsideRange,
    /// <summary>Refuses a PGM header without the raster whitespace delimiter.</summary>
    PgmRasterDelimiterMissing,
    /// <summary>Refuses a PGM raster shorter than its declared dimensions require.</summary>
    PgmRasterTruncated,
    /// <summary>Refuses a PGM header field without a preceding separator.</summary>
    PgmHeaderSeparatorMissing,
    /// <summary>Refuses a missing or non-decimal PGM header integer.</summary>
    PgmHeaderIntegerMalformed,
    /// <summary>Refuses a PGM header integer above the supported integer range.</summary>
    PgmHeaderIntegerOverflow,

    // Clearance declaration admission and editing.
    /// <summary>Refuses a clearance mesh root that is rooted instead of relative.</summary>
    ClearanceMeshRootRooted,
    /// <summary>Refuses a clearance mesh root containing a backslash.</summary>
    ClearanceMeshRootBackslash,
    /// <summary>Refuses a clearance sampling cell size outside the permitted range.</summary>
    ClearanceCellSizeOutsideRange,
    /// <summary>Refuses a supplied clearance waiver tag that is blank.</summary>
    ClearanceWaiverTagBlank,
    /// <summary>Refuses a duplicate clearance rule name.</summary>
    ClearanceRuleDuplicate,
    /// <summary>Refuses clearance rules without a non-blank mesh root.</summary>
    ClearanceMeshRootRequired,
    /// <summary>Refuses removing a clearance rule that does not exist.</summary>
    ClearanceRuleMissing,
    /// <summary>Refuses a null clearance rule entry.</summary>
    ClearanceRuleNull,
    /// <summary>Refuses a clearance rule without a non-blank name.</summary>
    ClearanceRuleNameBlank,
    /// <summary>Refuses a clearance tolerance that is negative or non-finite.</summary>
    ClearanceToleranceInvalid,
    /// <summary>Refuses a clearance rule with a missing or empty tag list.</summary>
    ClearanceTagsEmpty,
    /// <summary>Refuses a clearance rule tag that is null or blank.</summary>
    ClearanceTagBlank,
    /// <summary>Refuses a clearance file block whose rule list is null.</summary>
    ClearanceRulesNull,
    /// <summary>Refuses a clearance file block whose mesh root is null.</summary>
    ClearanceMeshRootNull,
    /// <summary>Refuses editing a clearance rule without a clearance block.</summary>
    ClearanceBlockMissing,

    // Foliage layer validation and document admission.
    /// <summary>Refuses a foliage layer whose id is null or blank.</summary>
    FoliageIdBlank,
    /// <summary>Refuses a foliage layer on a negative plane.</summary>
    FoliagePlaneNegative,
    /// <summary>Refuses a foliage raster with a non-finite origin coordinate.</summary>
    FoliageOriginNonFinite,
    /// <summary>Refuses a foliage cell size that is non-finite or not positive.</summary>
    FoliageCellSizeInvalid,
    /// <summary>Refuses a foliage raster with a non-positive dimension.</summary>
    FoliageDimensionsNonPositive,
    /// <summary>Refuses a foliage raster exceeding the supported sample count.</summary>
    FoliageSampleLimitExceeded,
    /// <summary>Refuses foliage density whose length differs from its raster dimensions.</summary>
    FoliageDensityLengthMismatch,
    /// <summary>Refuses foliage spacing that is non-finite or not positive.</summary>
    FoliageSpacingInvalid,
    /// <summary>Refuses a foliage scale range that is non-finite, non-positive or unordered.</summary>
    FoliageScaleRangeInvalid,
    /// <summary>Refuses a non-finite foliage root offset.</summary>
    FoliageRootOffsetNonFinite,
    /// <summary>Refuses a foliage door clearance that is negative or non-finite.</summary>
    FoliageDoorClearanceInvalid,
    /// <summary>Refuses a foliage edge fade that is negative or non-finite.</summary>
    FoliageEdgeFadeInvalid,
    /// <summary>Refuses a foliage layer without an archetype.</summary>
    FoliageArchetypesEmpty,
    /// <summary>Refuses a foliage archetype whose id is null or blank.</summary>
    FoliageArchetypeIdBlank,
    /// <summary>Refuses a repeated ordinal foliage archetype id.</summary>
    FoliageArchetypeDuplicate,
    /// <summary>Refuses a foliage archetype weight that is non-finite or not positive.</summary>
    FoliageWeightInvalid,
    /// <summary>Refuses foliage archetype weights whose accumulated total is non-finite.</summary>
    FoliageTotalWeightNonFinite,
    /// <summary>Refuses void material zero as an allowed foliage underlay.</summary>
    FoliageUnderlayVoid,
    /// <summary>Refuses a null foliage layer entry in a manifest.</summary>
    FoliageLayerNull,
    /// <summary>Refuses a foliage manifest layer whose archetype list is null.</summary>
    FoliageArchetypesNull,
    /// <summary>Refuses a null foliage archetype entry in a manifest layer.</summary>
    FoliageArchetypeNull,
    /// <summary>Refuses a repeated ordinal foliage layer id.</summary>
    FoliageLayerDuplicate,
    /// <summary>Refuses a foliage layer on a plane the world does not have.</summary>
    FoliagePlaneOutsideWorld,
    /// <summary>Refuses a foliage manifest layer whose density is null.</summary>
    FoliageDensityNull,

    // Required foliage lookups and catalog references.
    /// <summary>Refuses a required lookup for a foliage layer that does not exist.</summary>
    FoliageLayerMissing,
    /// <summary>Refuses a foliage configuration referencing an archetype absent from the catalogs.</summary>
    FoliageCatalogArchetypeMissing,
    /// <summary>Refuses a foliage configuration allowing an underlay absent from the catalogs.</summary>
    FoliageCatalogUnderlayMissing,

    // Byte-preserving catalog collision-height editing.
    /// <summary>Refuses an archetype height that is not finite and positive.</summary>
    CatalogEditHeightInvalid,
    /// <summary>Refuses catalog bytes that cannot be read as JSON.</summary>
    CatalogEditJsonMalformed,
    /// <summary>Refuses a catalog edit whose source file cannot be read.</summary>
    CatalogEditReadFailed,
    /// <summary>Refuses catalog editing when the JSON root is not an object.</summary>
    CatalogEditNotObject,
    /// <summary>Refuses catalog editing when requested archetypes are absent.</summary>
    CatalogEditArchetypesMissing,
    /// <summary>Refuses catalog editing when a requested archetype is defined more than once.</summary>
    CatalogEditArchetypeDuplicate,
    /// <summary>Refuses catalog editing when a requested archetype has repeated collisionHeight properties.</summary>
    CatalogEditHeightDuplicate,

    // TileEdit session lifecycle and save admission.
    /// <summary>Refuses opening a world when loading fails without a known tile-world refusal.</summary>
    SessionOpenFailed,
    /// <summary>Refuses creating a world in a directory that already holds a world manifest.</summary>
    SessionWorldExists,
    /// <summary>Refuses an operation requiring an open world before one has been opened or created.</summary>
    SessionNotOpen,
    /// <summary>Refuses saving a world with clearance findings.</summary>
    SessionClearanceFailed,

    // Prefab discovery.
    /// <summary>Refuses listing prefabs in a resolved directory that does not exist.</summary>
    PrefabDirectoryMissing,

    // Catalog ground material sets.
    /// <summary>Refuses a catalog exceeding one material set's capacity after reserving the missing layer.</summary>
    GroundMaterialLayerLimitExceeded,
    /// <summary>Refuses a ground texture whose dimensions differ from the first textured material.</summary>
    GroundMaterialTextureSizeMismatch,
    /// <summary>Refuses a relative ground texture when its catalog has no source file.</summary>
    GroundMaterialTextureSourceMissing,

    // Authored colour parsing.
    /// <summary>Refuses a null authored colour.</summary>
    ColourNull,
    /// <summary>Refuses an authored colour without a leading hash and six or eight digits.</summary>
    ColourShapeInvalid,
    /// <summary>Refuses a non-hex character in a correctly shaped authored colour.</summary>
    ColourDigitInvalid,

    // Placed collider geometry.
    /// <summary>Refuses a collidable placed archetype without a collision height.</summary>
    ColliderCollisionHeightMissing,

    // Catalog collision boxes.
    /// <summary>Refuses collision boxes on an archetype whose collision kind builds no object collider.</summary>
    CatalogCollisionBoxKindUnsupported,
    /// <summary>Refuses a null entry in an archetype's collision boxes.</summary>
    CatalogCollisionBoxNull,
    /// <summary>Refuses a non-finite collision-box bound or yaw.</summary>
    CatalogCollisionBoxNonFinite,
    /// <summary>Refuses a collision box whose minimum is not below its maximum on an axis, so it is inverted or
    /// empty.</summary>
    CatalogCollisionBoxBoundsUnordered,
}
