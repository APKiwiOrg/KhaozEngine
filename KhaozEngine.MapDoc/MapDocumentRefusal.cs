namespace KhaozEngine.MapDoc;

/// <summary>The rule that refused a map document operation, independent of developer-facing message text.</summary>
public enum MapDocumentRefusal
{
    // Shared resolver contract.
    /// <summary>Refuses a document recipe or options identity incompatible with its resolver.</summary>
    ResolverContractMismatch,

    // Schema and scalar admission.
    /// <summary>Refuses malformed JSON or a malformed tile sculpt payload.</summary>
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

    // Shared identity admission.
    /// <summary>Refuses an identity repeated within its owning namespace.</summary>
    IdentityDuplicate,

    // Foliage JSON converter.
    /// <summary>Refuses a foliage section larger than its UTF-8 byte budget.</summary>
    FoliageSectionByteLimitExceeded,
    /// <summary>Refuses reading a foliage section that is not fully buffered.</summary>
    FoliageSectionIncomplete,
    /// <summary>Refuses an explicit null foliage section.</summary>
    FoliageSectionNull,
}
