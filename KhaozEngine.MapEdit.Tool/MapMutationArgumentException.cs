using System;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEdit;

/// <summary>A mutation argument refusal preserving its original ArgumentException base and parameter name.</summary>
public sealed class MapMutationArgumentException : ArgumentException
{
    public MapMutationArgumentException(MapDocumentRefusal reason, string message, string? paramName = null)
        : base(message, paramName) => Reason = reason;
    public MapMutationArgumentException(MapDocumentRefusal reason, string message, Exception inner, string? paramName = null)
        : base(message, paramName, inner) => Reason = reason;
    /// <summary>The mutation argument rule that refused the request.</summary>
    public MapDocumentRefusal Reason { get; }
    /// <summary>The refused tool verb.</summary>
    public string? Operation { get; init; }
    /// <summary>The field whose input was refused.</summary>
    public string? FieldPath { get; init; }
}
