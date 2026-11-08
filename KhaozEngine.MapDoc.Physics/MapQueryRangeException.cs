using System;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>A reach or height-band refusal retaining the ArgumentOutOfRangeException catch contract.</summary>
public sealed class MapQueryRangeException : ArgumentOutOfRangeException
{
    /// <summary>Creates a refusal retaining the caller's parameter name.</summary>
    public MapQueryRangeException(MapDocumentRefusal reason, string message, string? paramName)
        : base(paramName, message) => Reason = reason;
    /// <summary>Creates a refusal retaining a supplied actual value.</summary>
    public MapQueryRangeException(MapDocumentRefusal reason, string message, string? paramName, object? actualValue)
        : base(paramName, actualValue, message) => Reason = reason;
    /// <summary>The rule that refused the query.</summary>
    public MapDocumentRefusal Reason { get; }
    /// <summary>The failed reach, tolerance or band component.</summary>
    public string? FieldPath { get; init; }
    /// <summary>The submitted numeric component, or the lower endpoint of an inverted band.</summary>
    public double? ActualNumber { get; init; }
    /// <summary>The inclusive lower domain boundary, or the submitted band minimum.</summary>
    public double? MinimumNumber { get; init; }
    /// <summary>The submitted band maximum.</summary>
    public double? MaximumNumber { get; init; }
}
