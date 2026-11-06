using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEditor;

/// <summary>A command admission refusal preserving the InvalidOperationException catch contract.</summary>
public sealed class MapEditorCommandException : InvalidOperationException
{
    IReadOnlyList<string>? _sourceIds;

    /// <summary>Creates a refusal for the given command rule.</summary>
    public MapEditorCommandException(MapDocumentRefusal reason, string message) : base(message) => Reason = reason;
    /// <summary>Creates a refusal retaining its original cause.</summary>
    public MapEditorCommandException(MapDocumentRefusal reason, string message, Exception inner) : base(message, inner) => Reason = reason;

    /// <summary>The rule that refused the edit.</summary>
    public MapDocumentRefusal Reason { get; }
    /// <summary>The document whose edit was refused.</summary>
    public string? DocumentId { get; init; }
    /// <summary>The kind of missing, repeated or referenced element.</summary>
    public string? SubjectKind { get; init; }
    /// <summary>The requested or colliding element identity.</summary>
    public string? EntityId { get; init; }
    /// <summary>The scatter or companion layer involved in the refusal.</summary>
    public string? LayerId { get; init; }
    /// <summary>The document collection containing the refused identity.</summary>
    public string? FieldPath { get; init; }
    /// <summary>The zero-based colliding index, when the guard has one.</summary>
    public int? Index { get; init; }
    /// <summary>Referencing companion names, then exclusion and scatter-override index paths, in document order.
    /// Setting this property copies the identifiers into an owned read-only snapshot.</summary>
    public IReadOnlyList<string>? SourceIds
    {
        get => _sourceIds;
        init => _sourceIds = value is null ? null : Array.AsReadOnly<string>([.. value]);
    }
}
