using System;
using System.Collections.Generic;

namespace KhaozEngine.MapDoc;

/// <summary>Thrown when a map document fails to load, validate, migrate, or save. Map documents are
/// dev-authored content, so failures are loud (a server refuses to boot on a bad document) rather than
/// quarantined like runtime player-state blobs.</summary>
public sealed class MapDocumentException : Exception
{
    IReadOnlyList<MapDocumentFinding>? _findings;

    public MapDocumentException(string message) : base(message) { }
    public MapDocumentException(string message, Exception inner) : base(message, inner) { }

    /// <summary>Creates a refusal with its stable reason and developer-facing message.</summary>
    public MapDocumentException(MapDocumentRefusal reason, string message) : base(message) => Reason = reason;

    /// <summary>Creates a refusal retaining its original cause and stable reason.</summary>
    public MapDocumentException(MapDocumentRefusal reason, string message, Exception inner) : base(message, inner) => Reason = reason;

    /// <summary>The stable refusal reason. Null only for an instance built through a message-only constructor
    /// by consumer code outside the engine.</summary>
    public MapDocumentRefusal? Reason { get; }

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
    /// <summary>The document or options field whose contract was refused. JSON fields use
    /// <see cref="System.Text.Json.JsonException.Path"/> format: <c>$</c> for the root, <c>.name</c> for
    /// a plain member, <c>['name']</c> for a member containing special characters, and <c>[index]</c>
    /// for an array element.</summary>
    public string? FieldPath { get; init; }
    /// <summary>The kind of document content requiring this resolver contract.</summary>
    public string? SubjectKind { get; init; }

    /// <summary>The complete ordered findings from a typed validator. Setting this property takes an owned,
    /// read-only snapshot, so changing the input collection or casting the exposed list cannot mutate it.</summary>
    public IReadOnlyList<MapDocumentFinding>? Findings
    {
        get => _findings;
        init => _findings = value is null ? null : Array.AsReadOnly<MapDocumentFinding>([.. value]);
    }

    /// <summary>Adds source context while preserving the reason, every identifier and this refusal as the cause.
    /// A supplied wrapper message retains the caller's released wording. Existing tile and path identifiers
    /// take precedence over additional source context. A null source name keeps the existing source,
    /// and an existing resource identity takes precedence over an additional resource identity.</summary>
    internal MapDocumentException WithSourceName(string? sourceName, string? message = null,
        MapTileCoord? tile = null, string? path = null, string? resourceId = null) => new(this, sourceName, message, tile, path, resourceId);

    MapDocumentException(MapDocumentException inner, string? sourceName, string? message,
        MapTileCoord? tile, string? path, string? resourceId) : base(message ?? $"{sourceName}: {inner.Message}", inner)
    {
        Reason = inner.Reason;
        SourceName = sourceName ?? inner.SourceName;
        Path = inner.Path ?? path;
        ResourceId = inner.ResourceId ?? resourceId;
        Width = inner.Width;
        Depth = inner.Depth;
        Tile = inner.Tile ?? tile;
        LineNumber = inner.LineNumber;
        BytePositionInLine = inner.BytePositionInLine;
        PayloadVersion = inner.PayloadVersion;
        ResolverVersion = inner.ResolverVersion;
        OptionsResolverVersion = inner.OptionsResolverVersion;
        ExpectedResolverVersion = inner.ExpectedResolverVersion;
        SupportRecipe = inner.SupportRecipe;
        ExpectedSupportRecipe = inner.ExpectedSupportRecipe;
        FieldPath = inner.FieldPath;
        SubjectKind = inner.SubjectKind;
        // Findings already owns a read-only snapshot whose elements are immutable records.
        _findings = inner._findings;
    }
}
