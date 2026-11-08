using System;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>A scope admission refusal retaining the ArgumentException catch contract.</summary>
public sealed class MapSurfaceScopeArgumentException : ArgumentException
{
    /// <summary>Creates a scope refusal with its stable rule and released diagnostic.</summary>
    public MapSurfaceScopeArgumentException(MapDocumentRefusal reason, string message) : base(message) => Reason = reason;
    /// <summary>Creates a scope refusal retaining its original cause.</summary>
    public MapSurfaceScopeArgumentException(MapDocumentRefusal reason, string message, Exception inner) : base(message, inner) => Reason = reason;
    /// <summary>The rule that refused the scope.</summary>
    public MapDocumentRefusal Reason { get; }
    /// <summary>The failed scope field or nested limit.</summary>
    public string? FieldPath { get; init; }
    /// <summary>The refused numeric value.</summary>
    public double? ActualNumber { get; init; }
    /// <summary>The inclusive minimum of the named numeric field.</summary>
    public double? MinimumNumber { get; init; }
    /// <summary>The refused integer limit or enum value.</summary>
    public long? ActualInteger { get; init; }
    /// <summary>The inclusive minimum of the named integer field.</summary>
    public long? MinimumInteger { get; init; }
    /// <summary>The refused text, when supplied.</summary>
    public string? ActualText { get; init; }
    /// <summary>The enumeration containing the refused value.</summary>
    public Type? EnumType { get; init; }
}
