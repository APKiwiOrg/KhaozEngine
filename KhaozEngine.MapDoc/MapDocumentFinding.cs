using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Assets;

namespace KhaozEngine.MapDoc;

/// <summary>An immutable validation finding. Message is its prose projection, while Reason and the applicable
/// identifiers describe the refused rule. Identifiers not known by the producing guard remain null.</summary>
/// <param name="Reason">The rule refused by the validator.</param>
/// <param name="Message">The developer-facing explanation, not a classification token.</param>
public sealed record MapDocumentFinding(MapDocumentRefusal Reason, string Message)
{
    IReadOnlyList<int>? _supportedVersions;

    /// <summary>The source file or content name, when known.</summary>
    public string? SourceName { get; init; }
    /// <summary>The source file path, when known separately from the source name.</summary>
    public string? Path { get; init; }
    /// <summary>The resource being decoded, when known.</summary>
    public string? ResourceId { get; init; }
    /// <summary>The refused patch width, when checked.</summary>
    public int? Width { get; init; }
    /// <summary>The refused patch depth, when checked.</summary>
    public int? Depth { get; init; }
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
    /// <summary>The kind of document content or identity whose rule was refused.</summary>
    public string? SubjectKind { get; init; }

    /// <summary>The foliage layer identity, after it has been read.</summary>
    public string? LayerId { get; init; }
    /// <summary>The zero-based position of the containing or refused JSON array item, when known.</summary>
    public int? Index { get; init; }
    /// <summary>The foliage chunk coordinates, after both coordinates have been read.</summary>
    public MapFoliageChunkKey? FoliageChunk { get; init; }
    /// <summary>The measured or decoded byte count, when the whole value can be measured.</summary>
    public long? ActualByteCount { get; init; }
    /// <summary>The required decoded byte count, when checked.</summary>
    public long? ExpectedByteCount { get; init; }
    /// <summary>The inclusive byte or work limit, when checked.</summary>
    public long? Limit { get; init; }
    /// <summary>The refused payload version, when read as an integer.</summary>
    public int? ActualVersion { get; init; }
    /// <summary>The refused numeric value, when representable as a double.</summary>
    public double? ActualNumber { get; init; }
    /// <summary>The refused numeric value, when representable as a signed 64-bit integer.</summary>
    public long? ActualInteger { get; init; }
    /// <summary>The inclusive minimum integer accepted by the checked field.</summary>
    public long? MinimumInteger { get; init; }
    /// <summary>The inclusive maximum integer accepted by the checked field.</summary>
    public long? MaximumInteger { get; init; }
    /// <summary>The refused identity, such as a repeated JSON member name, when known.</summary>
    public string? EntityId { get; init; }

    /// <summary>The asset descriptor whose admission failed, when known.</summary>
    public string? AssetId { get; init; }
    /// <summary>The declared surface whose metadata failed validation, when known.</summary>
    public string? SurfaceId { get; init; }
    /// <summary>The placement whose admission failed, when known.</summary>
    public string? PlacementId { get; init; }
    /// <summary>The referenced resource kind required by the asset.</summary>
    public MapResourceKind? ExpectedResourceKind { get; init; }
    /// <summary>The declared resource kind, when a resource is available.</summary>
    public MapResourceKind? ActualResourceKind { get; init; }
    /// <summary>The refused numeric identity, when present.</summary>
    public long? NumericId { get; init; }
    /// <summary>The reserved numeric identity high-water mark checked by the guard.</summary>
    public long? HighWaterMark { get; init; }
    /// <summary>The minimum of the checked numeric domain, or the submitted lower endpoint when bounds are invalid.</summary>
    public double? MinimumNumber { get; init; }
    /// <summary>The maximum of the checked numeric domain, or the submitted upper endpoint when bounds are invalid.</summary>
    public double? MaximumNumber { get; init; }

    /// <summary>The supported payload versions, as an owned read-only snapshot.</summary>
    public IReadOnlyList<int>? SupportedVersions
    {
        get => _supportedVersions;
        init => _supportedVersions = value is null ? null : Array.AsReadOnly<int>([.. value]);
    }

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
