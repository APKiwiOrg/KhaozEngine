namespace KhaozEngine.MapDoc;

/// <summary>The rule that refused a map document operation, independent of developer-facing message text.</summary>
public enum MapDocumentRefusal
{
    // Shared resolver contract.
    /// <summary>Refuses a document recipe or options identity incompatible with its resolver.</summary>
    ResolverContractMismatch,
    /// <summary>Refuses legacy resolver work without an analytic support callback.</summary>
    SupportLegacyHeightRequired,

    // Schema and scalar admission.
    /// <summary>Refuses malformed JSON, typed payload encoding or tile sculpt data.</summary>
    SchemaMalformed,
    /// <summary>Refuses a repeated or case-variant JSON member.</summary>
    SchemaMemberDuplicate,
    /// <summary>Refuses a missing required member or a missing or non-integer format version.</summary>
    SchemaMemberMissing,
    /// <summary>Refuses a native member that cannot be null.</summary>
    SchemaMemberNull,
    /// <summary>Refuses a member not declared by a closed schema object.</summary>
    SchemaMemberUnknown,
    /// <summary>Refuses a JSON value that is not the required object.</summary>
    SchemaObjectRequired,
    /// <summary>Refuses a missing, null, blank or incorrectly typed required value.</summary>
    SchemaValueRequired,
    /// <summary>Refuses bounds or dimensions outside the ordered nonempty finite domain.</summary>
    BoundsInvalid,
    /// <summary>Refuses a collection or payload whose shape differs from its declared count.</summary>
    SchemaArrayLengthMismatch,
    /// <summary>Refuses a JSON value that is not the required array.</summary>
    SchemaArrayRequired,
    /// <summary>Refuses malformed or noncanonical base64 data.</summary>
    SchemaBase64Malformed,
    /// <summary>Refuses a JSON value that is not a boolean.</summary>
    SchemaBooleanRequired,
    /// <summary>Refuses a payload version outside the supported set.</summary>
    SchemaPayloadVersionUnsupported,
    /// <summary>Refuses a numeric field requiring a finite value.</summary>
    ValueNonfinite,
    /// <summary>Refuses a numeric field outside its stated domain.</summary>
    ValueOutsideRange,
    /// <summary>Refuses a digest outside its canonical lowercase SHA-256 representation.</summary>
    DigestMalformed,
    /// <summary>Refuses a value outside the declared enumeration.</summary>
    SchemaEnumUnsupported,

    /// <summary>Refuses text outside canonical signed 64-bit decimal representation.</summary>
    SchemaInt64Malformed,

    // Shared identity admission.
    /// <summary>Refuses foliage editing without a verified asset closure.</summary>
    AssetClosureRequired,
    /// <summary>Refuses authored ownership inconsistent with its standalone owner or retained source.</summary>
    AuthoredOwnershipMismatch,
    /// <summary>Refuses reuse of an identity held by a retained owner.</summary>
    IdentityReserved,
    /// <summary>Refuses a target missing from the required authored or captured scope.</summary>
    ReferenceMissing,
    /// <summary>Refuses bytes, a descriptor or surface semantics whose digest differs from its pin.</summary>
    DigestMismatch,
    /// <summary>Refuses an identity repeated within its owning namespace.</summary>
    IdentityDuplicate,
    /// <summary>Refuses a persisted sequence outside its required canonical ordering.</summary>
    OrderInvalid,
    /// <summary>Refuses an operation or record kind unsupported by its editor or codec.</summary>
    OperationKindUnsupported,
    /// <summary>Refuses a numeric identity above its reserved high-water mark.</summary>
    NumericIdHighWaterExceeded,
    /// <summary>Refuses a referenced resource or topology record of a different kind than required.</summary>
    ReferenceKindMismatch,

    /// <summary>Refuses an authored reference of a child kind or topology type forbidden by its role.</summary>
    AuthoredReferenceKindInvalid,

    /// <summary>Refuses an authored replacement or reference differing from its declared target.</summary>
    AuthoredValueMismatch,
    /// <summary>Refuses an authored prefab value required by its parent or reference contract.</summary>
    AuthoredValueRequired,

    // Asset manifest admission.
    /// <summary>Refuses an undefined resource kind.</summary>
    AssetKindUnsupported,

    // Asset closure and typed payload admission.
    /// <summary>Refuses a cycle in the resource dependency graph.</summary>
    AssetDependencyCycle,
    /// <summary>Refuses access to a resource payload not captured in the closure.</summary>
    AssetPayloadNotCaptured,
    /// <summary>Refuses a required resource whose bytes cannot be read or decoded.</summary>
    AssetPayloadUnreadable,
    /// <summary>Refuses a resource path owned by a map storage writer.</summary>
    AssetStoragePathReserved,
    /// <summary>Refuses a typed consumer given an opaque resource.</summary>
    AssetTypedPayloadOpaque,

    /// <summary>Refuses a descriptor differing from its verified closure descriptor.</summary>
    AssetDescriptorMismatch,
    /// <summary>Refuses an external buffer or image URI in a self-contained native mesh.</summary>
    AssetGlbExternalUri,
    /// <summary>Refuses bytes remaining after the collision shape.</summary>
    AssetCollisionTrailingBytes,
    /// <summary>Refuses a collision hull or mesh without points.</summary>
    AssetCollisionEmptyPoints,
    /// <summary>Refuses a triangle mesh nested inside a compound collider.</summary>
    AssetCollisionNestedMesh,

    // Prefab edit admission.
    /// <summary>Refuses an absent or removed prefab instance.</summary>
    PrefabInstanceMissing,
    /// <summary>Refuses removing a child already held by a remove override.</summary>
    PrefabChildAlreadyRemoved,
    /// <summary>Refuses multiple initial overrides of the same typed child.</summary>
    PrefabOverrideDuplicate,
    /// <summary>Refuses an extraction selection outside the required World reference scope.</summary>
    PrefabExtractionReferenceInvalid,
    /// <summary>Refuses extraction without selected children.</summary>
    PrefabExtractionEmpty,
    /// <summary>Refuses extracting an attachment owner without every live associated child.</summary>
    PrefabExtractionAttachmentIncomplete,
    /// <summary>Refuses partial room, floor or roof extraction that is not lossless.</summary>
    PrefabExtractionNotLossless,

    // Prefab structural admission.
    /// <summary>Refuses a remove override whose Previous is another remove override.</summary>
    PrefabPreviousOverrideNestedRemove,
    /// <summary>Refuses a prefab instance or asset update whose verified pins disagree.</summary>
    PrefabAssetPinMismatch,
    /// <summary>Refuses adding a child that already exists.</summary>
    PrefabAddedChildExists,
    /// <summary>Refuses a prefab resource or child key differing from its payload identity or kind.</summary>
    PrefabPayloadIdMismatch,
    /// <summary>Refuses a placement child carrying an additional local transform.</summary>
    PrefabPlacementTransformDuplicated,
    /// <summary>Refuses a prefab record anchored outside a declared local surface.</summary>
    PrefabLocalAnchorInvalid,

    // Prefab leaf resource admission.
    /// <summary>Refuses a leaf light owner other than null or its Local placement token.</summary>
    PrefabLightOwnerInvalid,

    // Prefab bindings and reference admission.
    /// <summary>Refuses allocation beyond the signed 64-bit numeric identity space.</summary>
    PrefabNumericIdSpaceExhausted,
    /// <summary>Refuses a reference occurrence without an explicit or inherited scope.</summary>
    PrefabReferenceScopeMissing,
    /// <summary>Refuses an effective light whose world placement owner is missing.</summary>
    PrefabEffectiveLightOwnerMissing,
    /// <summary>Refuses materialized rows for one placement with different captured frames.</summary>
    PrefabMaterializedFrameMismatch,
    /// <summary>Refuses a materialized record anchored outside its row's captured surfaces.</summary>
    PrefabMaterializedAnchorMismatch,
    /// <summary>Refuses an effective paint override without its target surface.</summary>
    PrefabPaintTargetRequired,
    /// <summary>Refuses a static whose world position exceeds the installed limit on any axis.</summary>
    PrefabWorldCoordinateLimitExceeded,

    // Marker authoring admission.
    /// <summary>Refuses adoption choices that do not cover exactly the source spawns.</summary>
    MarkerAdoptionChoiceSetMismatch,
    /// <summary>Refuses adoption without a destination and height choice for a source spawn.</summary>
    MarkerAdoptionChoiceMissing,
    /// <summary>Refuses a repeated effective or destination marker name.</summary>
    MarkerNameDuplicate,
    /// <summary>Refuses a marker height outside its canonical authored encoding.</summary>
    MarkerHeightChoiceInvalid,
    /// <summary>Refuses marker authoring without a native document.</summary>
    MarkerNativeDocumentRequired,
    /// <summary>Refuses direct editing of a live prefab-owned marker.</summary>
    MarkerPrefabOwnershipProtected,

    // Authored support resolution.
    /// <summary>Refuses a marker whose support query did not succeed.</summary>
    MarkerSupportUnresolved,
    /// <summary>Refuses a placement whose support query did not succeed.</summary>
    SupportUnresolved,
    /// <summary>Refuses support supplied by a placement with no explicit height.</summary>
    SupportProviderHeightMissing,
    /// <summary>Refuses a missing authored placement support binding.</summary>
    SupportBindingMissing,
    /// <summary>Refuses a space binding without reference height and bounded search.</summary>
    SupportSpaceSearchMissing,
    /// <summary>Refuses an undeclared support space.</summary>
    SupportSpaceMissing,
    /// <summary>Refuses adoption without exactly one explicit height or authored support choice.</summary>
    SupportAdoptionChoiceMissing,

    // Bounded work admission.
    /// <summary>Refuses authored work or metadata beyond its checked capacity.</summary>
    WorkCapacityExceeded,

    // Exact geometry admission.
    /// <summary>Refuses geometry whose effective representation collapses or exceeds its exact domain.</summary>
    GeometryNotRepresentable,
    /// <summary>Refuses a vertex sequence lacking the required ordered correspondence or cardinality.</summary>
    GeometryVertexSequenceInvalid,

    // Surface geometry and payload admission.
    /// <summary>Refuses different exact heights for a shared surface vertex.</summary>
    GeometryVertexHeightMismatch,
    /// <summary>Refuses geometry outside the local frame radius or its checked anchor domain.</summary>
    FrameRadiusExceeded,
    /// <summary>Refuses an effective patch unavailable from the acquired view.</summary>
    PatchUnavailable,
    /// <summary>Refuses legacy sampling outside the selected patch cell.</summary>
    SurfaceLegacySampleOutOfCell,
    /// <summary>Refuses physical-face compilation of a paint-only surface.</summary>
    SurfacePaintPhysicalCompileUnsupported,
    /// <summary>Refuses legacy NoDraw flags on a native surface cell.</summary>
    SurfaceNativeNoDrawUnsupported,
    /// <summary>Refuses paired boundary or corner vertices at different exact world positions.</summary>
    SurfaceSeamPositionMismatch,
    /// <summary>Refuses a seam or corner vertex absent from its declared source geometry.</summary>
    SurfaceSeamVertexUndeclared,
    /// <summary>Refuses a collapsed or non-axis-aligned seam source edge.</summary>
    SurfaceSeamEdgeInvalid,
    /// <summary>Refuses a support resource surface outside the native support-floor policy.</summary>
    SurfaceSupportRoleInvalid,
    /// <summary>Refuses a wall strip whose lower chain is above its upper chain.</summary>
    SurfaceWallHeightInverted,
    /// <summary>Refuses paint compilation without complete captured geometry or the required paint role.</summary>
    AcquisitionIncomplete,
    /// <summary>Refuses a paint override incompatible with its declared physical target.</summary>
    SurfacePaintTargetInvalid,
    /// <summary>Refuses nonzero paint priority on a surface other than PaintOverride.</summary>
    SurfacePaintPriorityMisplaced,
    /// <summary>Refuses overlapping equal-priority paint with different materials.</summary>
    SurfacePaintPriorityConflict,
    /// <summary>Refuses an open or nonmanifold paint material boundary.</summary>
    SurfacePaintBoundaryInvalid,
    /// <summary>Refuses whole-face paint whose material or presence boundary crosses a physical face.</summary>
    SurfacePaintCrossesPhysicalFace,

    // Scoped acquisition producer admission.
    /// <summary>Refuses acquisition facts differing from the session's captured snapshot.</summary>
    AcquisitionSnapshotChanged,
    /// <summary>Refuses a producer result inconsistent with its declared acquisition contract.</summary>
    AcquisitionContractMismatch,
    /// <summary>Refuses a source without bounded scoped acquisition support.</summary>
    AcquisitionUnsupported,

    // Surface scalar and local patch admission.
    /// <summary>Refuses a lattice unit not in reduced form.</summary>
    SurfaceUnitUnreduced,
    /// <summary>Refuses a cell or corner coordinate outside its patch rectangle.</summary>
    SurfaceAddressOutOfPatch,
    /// <summary>Refuses nonzero bits beyond the authored cell count in a presence word.</summary>
    SurfacePresencePaddingNonzero,
    /// <summary>Refuses an edge subdivision on an absent cell.</summary>
    SurfaceSubdivisionAbsentCell,
    /// <summary>Refuses an edge subdivision adjoining another present cell inside the patch.</summary>
    SurfaceSubdivisionInteriorEdge,
    /// <summary>Refuses a forced diagonal incompatible with the cell's diagonal-half rotation.</summary>
    SurfaceForcedTopologyConflict,
    /// <summary>Refuses a patch payload with values remaining after its root.</summary>
    SurfacePatchTrailingBytes,
    /// <summary>Refuses a patch payload whose key differs from the requested key.</summary>
    SurfacePatchKeyMismatch,

    // Closed topology and legacy exterior admission.
    /// <summary>Refuses a legacy exterior recipe attached to a non-Exterior space.</summary>
    LegacyExteriorSpaceRequired,
    /// <summary>Refuses legacy exterior coverage without an open-top upper bound.</summary>
    LegacyExteriorOpenTopRequired,
    /// <summary>Refuses legacy-cell coverage or sampling through an incompatible surface recipe.</summary>
    SurfaceLegacyRecipeRequired,
    /// <summary>Refuses prism ancestry inconsistent with its Room space.</summary>
    SpacePrismParentMismatch,
    /// <summary>Refuses Room occupancy outside its single prism domain.</summary>
    SpaceRoomOccupancyInvalid,
    /// <summary>Refuses cyclic space ancestry.</summary>
    SpaceParentCycle,
    /// <summary>Refuses chain heights inconsistent with the authored or surface-edge kind.</summary>
    SpaceChainHeightKindMismatch,
    /// <summary>Refuses an open-top portal whose endpoints are not both Exterior spaces.</summary>
    SpacePortalOpenTopInvalid,
    /// <summary>Refuses portal membership inconsistent with its endpoint and side.</summary>
    SpacePortalMembershipMismatch,
    /// <summary>Refuses vertical-link membership inconsistent with its endpoint spaces.</summary>
    SpaceLinkMembershipMismatch,
    /// <summary>Refuses a space alias targeting another alias or a space of different ancestry or kind.</summary>
    SpaceAliasPeerInvalid,
    /// <summary>Refuses an open-top bound used below a space or carrying incompatible space or surface fields.</summary>
    SpaceOpenTopInvalid,
    /// <summary>Refuses a surface bound whose kind or surface role is incompatible with its lower or upper role.</summary>
    SpaceBoundRoleMismatch,
    /// <summary>Refuses a horizontal opening over a present cell.</summary>
    SpaceOpeningCellsInvalid,

    // Foliage JSON converter.
    /// <summary>Refuses a foliage section larger than its UTF-8 byte budget.</summary>
    FoliageSectionByteLimitExceeded,
    /// <summary>Refuses reading a foliage section that is not fully buffered.</summary>
    FoliageSectionIncomplete,
    /// <summary>Refuses an explicit null foliage section.</summary>
    FoliageSectionNull,
}
