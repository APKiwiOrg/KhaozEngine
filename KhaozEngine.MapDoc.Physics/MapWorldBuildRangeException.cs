using System;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>A world-build policy refusal preserving the ArgumentOutOfRangeException catch contract.</summary>
public sealed class MapWorldBuildRangeException : ArgumentOutOfRangeException
{
    /// <summary>Creates a refusal retaining the parameter name.</summary>
    public MapWorldBuildRangeException(MapDocumentRefusal reason, string message, string? paramName)
        : base(paramName, message) => Reason = reason;
    /// <summary>Creates a refusal retaining a supplied actual value.</summary>
    public MapWorldBuildRangeException(MapDocumentRefusal reason, string message, string? paramName, object? actualValue)
        : base(paramName, actualValue, message) => Reason = reason;

    /// <summary>The rule that refused the world-build policy.</summary>
    public MapDocumentRefusal Reason { get; }
    /// <summary>The nested option or chunk-policy field that failed admission.</summary>
    public string? FieldPath { get; init; }
    /// <summary>The submitted integer value.</summary>
    public long? ActualInteger { get; init; }
    /// <summary>The inclusive minimum accepted integer.</summary>
    public long? MinimumInteger { get; init; }
    /// <summary>The inclusive maximum accepted integer, when bounded above.</summary>
    public long? MaximumInteger { get; init; }
}
