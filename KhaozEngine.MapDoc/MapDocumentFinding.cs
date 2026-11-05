namespace KhaozEngine.MapDoc;

/// <summary>An immutable validation finding. Message is its prose projection, while Reason and the applicable
/// identifiers describe the refused rule. Identifiers not known by the producing guard remain null.</summary>
/// <param name="Reason">The rule refused by the validator.</param>
/// <param name="Message">The developer-facing explanation, not a classification token.</param>
public sealed record MapDocumentFinding(MapDocumentRefusal Reason, string Message)
{
    /// <summary>The source file or content name, when known.</summary>
    public string? SourceName { get; init; }
    /// <summary>The source file path, when known separately from the source name.</summary>
    public string? Path { get; init; }
    /// <summary>The document tile being read, when known.</summary>
    public MapTileCoord? Tile { get; init; }
    /// <summary>The zero-based JSON line number exposed by the reader, when available.</summary>
    public long? LineNumber { get; init; }
    /// <summary>The zero-based UTF-8 byte position within the JSON line, when exposed by the reader.</summary>
    public long? BytePositionInLine { get; init; }
    /// <summary>The resolver identity's declared payload version, when present.</summary>
    public int? PayloadVersion { get; init; }
    /// <summary>The document's declared resolver version, when present.</summary>
    public int? ResolverVersion { get; init; }
    /// <summary>The resolver version supplied in the caller's options, when checked.</summary>
    public int? OptionsResolverVersion { get; init; }
    /// <summary>The resolver version required by this operation, when one version is required.</summary>
    public int? ExpectedResolverVersion { get; init; }
    /// <summary>The document's declared support recipe, when known.</summary>
    public MapSupportRecipe? SupportRecipe { get; init; }
    /// <summary>The support recipe required by this operation, when one recipe is required.</summary>
    public MapSupportRecipe? ExpectedSupportRecipe { get; init; }
    /// <summary>The document or options field whose contract was refused.</summary>
    public string? FieldPath { get; init; }
    /// <summary>The kind of document content requiring this resolver contract.</summary>
    public string? SubjectKind { get; init; }

    internal static MapDocumentFinding ResolverContract(MapDocument document, string message, string fieldPath,
        string subjectKind, int? expectedResolverVersion = null, MapSupportRecipe? expectedSupportRecipe = null) =>
        new(MapDocumentRefusal.ResolverContractMismatch, message)
        {
            PayloadVersion = document.ResolverIdentity?.PayloadVersion,
            ResolverVersion = document.ResolverIdentity?.ResolverVersion,
            SupportRecipe = document.SupportRecipe,
            FieldPath = fieldPath,
            SubjectKind = subjectKind,
            ExpectedResolverVersion = expectedResolverVersion,
            ExpectedSupportRecipe = expectedSupportRecipe,
        };
}
