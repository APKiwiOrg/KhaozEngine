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
    /// <summary>Refuses a required document, tile, prefab or marker-options JSON object.</summary>
    SchemaObjectRequired,
    /// <summary>Refuses a missing, null or blank required value.</summary>
    SchemaValueRequired,
    /// <summary>Refuses bounds or dimensions outside the ordered nonempty finite domain.</summary>
    BoundsInvalid,
}
