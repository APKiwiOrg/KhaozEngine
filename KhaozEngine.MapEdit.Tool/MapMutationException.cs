using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEdit;

/// <summary>A mutation state refusal preserving the InvalidOperationException catch contract.</summary>
public sealed class MapMutationException : InvalidOperationException
{
    IReadOnlyList<MapDocumentFinding>? _findings;
    public MapMutationException(MapDocumentRefusal reason, string message) : base(message) => Reason = reason;
    public MapMutationException(MapDocumentRefusal reason, string message, Exception inner) : base(message, inner) => Reason = reason;
    /// <summary>The first producer rule or direct admission rule that refused the mutation.</summary>
    public MapDocumentRefusal Reason { get; }
    /// <summary>The document whose mutation was refused, when known.</summary>
    public string? DocumentId { get; init; }
    /// <summary>The missing or invalid entity identity.</summary>
    public string? EntityId { get; init; }
    /// <summary>The kind of missing or invalid entity.</summary>
    public string? SubjectKind { get; init; }
    /// <summary>The field whose contract failed.</summary>
    public string? FieldPath { get; init; }
    /// <summary>The scatter or companion layer identity, when known.</summary>
    public string? LayerId { get; init; }
    /// <summary>The placement whose rule failed, when known.</summary>
    public string? PlacementId { get; init; }
    /// <summary>The index of the invalid document item, when known.</summary>
    public int? Index { get; init; }
    /// <summary>All ordered producer findings and their complete typed context, copied as an owned read-only snapshot.</summary>
    public IReadOnlyList<MapDocumentFinding>? Findings
    {
        get => _findings;
        init => _findings = value is null ? null : Array.AsReadOnly<MapDocumentFinding>([.. value]);
    }

    internal static MapMutationException FromFindings(IReadOnlyList<MapDocumentFinding> findings, string message)
    {
        MapDocumentFinding first = findings[0];
        return new(first.Reason, message)
        {
            DocumentId = first.DocumentId, EntityId = first.EntityId, SubjectKind = first.SubjectKind,
            FieldPath = first.FieldPath, LayerId = first.LayerId, PlacementId = first.PlacementId,
            Index = first.Index, Findings = findings,
        };
    }
}
