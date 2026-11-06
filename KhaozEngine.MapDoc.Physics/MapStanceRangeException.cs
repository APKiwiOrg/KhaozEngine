using System;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>A stance policy refusal retaining the ArgumentOutOfRangeException catch contract.</summary>
public sealed class MapStanceRangeException : ArgumentOutOfRangeException
{
    /// <summary>Creates a refusal retaining the existing parameter and message.</summary>
    public MapStanceRangeException(MapDocumentRefusal reason, string message, string? paramName)
        : base(paramName, message) => Reason = reason;
    /// <summary>Creates a refusal retaining a supplied actual value.</summary>
    public MapStanceRangeException(MapDocumentRefusal reason, string message, string? paramName, object? actualValue)
        : base(paramName, actualValue, message) => Reason = reason;
    /// <summary>The rule that refused stance generation.</summary>
    public MapDocumentRefusal Reason { get; }
    /// <summary>The nested spacing or capsule field that failed.</summary>
    public string? FieldPath { get; init; }
    /// <summary>The submitted field value.</summary>
    public double? ActualNumber { get; init; }
    /// <summary>The domain boundary, inclusive for capsule half height and exclusive for spacing or radius.</summary>
    public double? MinimumNumber { get; init; }
    /// <summary>The work budget checked before the validator runs.</summary>
    public string? Budget { get; init; }
    /// <summary>The inclusive maximum number of proposals.</summary>
    public long? Limit { get; init; }
    /// <summary>The proposed count through the failing outline, when representable as an integer.</summary>
    public long? ActualCount { get; init; }
}
