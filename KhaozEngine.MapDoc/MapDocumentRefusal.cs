namespace KhaozEngine.MapDoc;

/// <summary>The rule that refused a map document operation, independent of developer-facing message text.</summary>
public enum MapDocumentRefusal
{
    // Shared resolver contract.
    /// <summary>Refuses a document recipe or options identity incompatible with its resolver.</summary>
    ResolverContractMismatch,

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
    /// <summary>Refuses a source-unit transform that is not finite and positive.</summary>
    TransformInvalid,

    // Shared identity admission.
    /// <summary>Refuses foliage editing without a verified asset closure.</summary>
    AssetClosureRequired,
    /// <summary>Refuses authored ownership inconsistent with its standalone owner or retained source.</summary>
    AuthoredOwnershipMismatch,
    /// <summary>Refuses reuse of an identity held by a retained owner.</summary>
    IdentityReserved,
    /// <summary>Refuses a target missing from the required authored or captured scope.</summary>
    ReferenceMissing,
    /// <summary>Refuses resource bytes or a descriptor whose digest differs from its pin.</summary>
    DigestMismatch,
    /// <summary>Refuses an identity repeated within its owning namespace.</summary>
    IdentityDuplicate,
    /// <summary>Refuses a numeric identity above its reserved high-water mark.</summary>
    NumericIdHighWaterExceeded,
    /// <summary>Refuses a referenced resource of a different kind than required.</summary>
    ReferenceKindMismatch,

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

    // Prefab leaf resource admission.
    /// <summary>Refuses a leaf light owner other than null or its Local placement token.</summary>
    PrefabLightOwnerInvalid,

    // Foliage JSON converter.
    /// <summary>Refuses a foliage section larger than its UTF-8 byte budget.</summary>
    FoliageSectionByteLimitExceeded,
    /// <summary>Refuses reading a foliage section that is not fully buffered.</summary>
    FoliageSectionIncomplete,
    /// <summary>Refuses an explicit null foliage section.</summary>
    FoliageSectionNull,
}
