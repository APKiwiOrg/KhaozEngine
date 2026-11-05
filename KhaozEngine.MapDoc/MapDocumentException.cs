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

    /// <summary>The complete ordered findings from a typed validator. Setting this property takes an owned,
    /// read-only snapshot, so changing the input collection or casting the exposed list cannot mutate it.</summary>
    public IReadOnlyList<MapDocumentFinding>? Findings
    {
        get => _findings;
        init => _findings = value is null ? null : Array.AsReadOnly<MapDocumentFinding>([.. value]);
    }

    /// <summary>Adds source context while preserving the reason, every identifier and this refusal as the cause.
    /// The wrapper message keeps the source prefix used by document loaders.</summary>
    internal MapDocumentException WithSourceName(string sourceName) => new(this, sourceName);

    MapDocumentException(MapDocumentException inner, string sourceName) : base($"{sourceName}: {inner.Message}", inner)
    {
        Reason = inner.Reason;
        SourceName = sourceName;
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
