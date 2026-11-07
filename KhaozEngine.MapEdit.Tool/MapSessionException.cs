using System;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEdit;

/// <summary>A refused session transition, catchable through the original InvalidOperationException base.</summary>
public sealed class MapSessionException : InvalidOperationException
{
    public MapSessionException(MapDocumentRefusal reason, string message) : base(message) => Reason = reason;
    public MapSessionException(MapDocumentRefusal reason, string message, Exception inner) : base(message, inner) => Reason = reason;

    /// <summary>The session rule that refused the transition.</summary>
    public MapDocumentRefusal Reason { get; }
    /// <summary>The open document, when known.</summary>
    public string? DocumentId { get; init; }
    /// <summary>The source storage path, when known.</summary>
    public string? Path { get; init; }
    /// <summary>The refused session operation.</summary>
    public string? Operation { get; init; }
    /// <summary>The state field that failed admission.</summary>
    public string? FieldPath { get; init; }
    /// <summary>The current storage form, when known.</summary>
    public MapDocumentForm? DocumentForm { get; init; }
    /// <summary>The storage form required by the transition, when applicable.</summary>
    public MapDocumentForm? ExpectedDocumentForm { get; init; }
}
