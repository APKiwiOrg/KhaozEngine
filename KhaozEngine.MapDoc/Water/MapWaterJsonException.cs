using System;
using System.Text.Json;

namespace KhaozEngine.MapDoc.Water;

/// <summary>A water codec refusal preserving the converter's JsonException catch contract.</summary>
public sealed class MapWaterJsonException : JsonException
{
    /// <summary>Creates a water JSON refusal with its stable reason.</summary>
    public MapWaterJsonException(MapDocumentRefusal reason, string message) : base(message) => Reason = reason;
    /// <summary>Creates a water JSON refusal retaining its original cause.</summary>
    public MapWaterJsonException(MapDocumentRefusal reason, string message, Exception inner) : base(message, inner) => Reason = reason;
    /// <summary>The rule that refused the water JSON value.</summary>
    public MapDocumentRefusal Reason { get; }
    /// <summary>The JSON field relative to the water section, or a forwarded scalar field.</summary>
    public string? FieldPath { get; init; }
    /// <summary>The submitted text rejected by a scalar or enumeration guard.</summary>
    public string? ActualText { get; init; }
    /// <summary>The refused numeric value, when representable as a double.</summary>
    public double? ActualNumber { get; init; }
    /// <summary>The refused integer value, when known.</summary>
    public long? ActualInteger { get; init; }
    /// <summary>The inclusive lower integer limit, when checked.</summary>
    public long? MinimumInteger { get; init; }
    /// <summary>The inclusive upper integer limit, when checked.</summary>
    public long? MaximumInteger { get; init; }
    /// <summary>The enumeration type whose submitted value is unsupported.</summary>
    public Type? EnumType { get; init; }

    // Keep the original refusal in the cause chain so document readers copy every identifier.
    internal static MapWaterJsonException FromDocument(MapDocumentException inner, string message) =>
        new(inner.Reason!.Value, message, inner)
        {
            FieldPath = inner.FieldPath, ActualText = inner.ActualText, ActualNumber = inner.ActualNumber,
            ActualInteger = inner.ActualInteger, MinimumInteger = inner.MinimumInteger, MaximumInteger = inner.MaximumInteger,
            EnumType = inner.EnumType,
        };
}
