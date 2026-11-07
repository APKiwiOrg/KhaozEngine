using System;
using System.Text.Json;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEdit;

/// <summary>A tool JSON admission refusal preserving the JsonException catch contract.</summary>
public sealed class MapToolJsonException : JsonException
{
    public MapToolJsonException(MapDocumentRefusal reason, string message) : base(message) => Reason = reason;
    public MapToolJsonException(MapDocumentRefusal reason, string message, Exception inner) : base(message, inner) => Reason = reason;
    /// <summary>The rule that refused the JSON value.</summary>
    public MapDocumentRefusal Reason { get; }
    /// <summary>The refused JSON field in JsonException.Path format.</summary>
    public string? FieldPath { get; init; }
    /// <summary>The document value being decoded.</summary>
    public string? SubjectKind { get; init; }
}
