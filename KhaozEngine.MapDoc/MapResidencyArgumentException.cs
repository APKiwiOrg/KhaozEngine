using System;

namespace KhaozEngine.MapDoc;

/// <summary>A residency configuration refusal preserving the ArgumentException catch contract.</summary>
public sealed class MapResidencyArgumentException : ArgumentException
{
    /// <summary>Creates a refusal retaining its parameter name.</summary>
    public MapResidencyArgumentException(MapDocumentRefusal reason, string message, string? paramName)
        : base(message, paramName) => Reason = reason;
    /// <summary>Creates a refusal retaining its parameter name and original cause.</summary>
    public MapResidencyArgumentException(MapDocumentRefusal reason, string message, string? paramName, Exception inner)
        : base(message, paramName, inner) => Reason = reason;

    /// <summary>The rule that refused the configuration.</summary>
    public MapDocumentRefusal Reason { get; }
    /// <summary>The specific configuration field that failed admission.</summary>
    public string? FieldPath { get; init; }
    /// <summary>The refused integer field value.</summary>
    public long? ActualInteger { get; init; }
    /// <summary>The inclusive minimum accepted by the named field.</summary>
    public long? MinimumInteger { get; init; }
}
