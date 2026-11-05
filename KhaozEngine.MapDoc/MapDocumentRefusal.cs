namespace KhaozEngine.MapDoc;

/// <summary>The rule that refused a map document operation, independent of developer-facing message text.</summary>
public enum MapDocumentRefusal
{
    // Shared resolver contract.
    /// <summary>Refuses a document recipe or options identity incompatible with its resolver.</summary>
    ResolverContractMismatch,
}
