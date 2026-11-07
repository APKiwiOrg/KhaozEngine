using System;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>A world-build identity refusal preserving the ArgumentException catch contract.</summary>
public sealed class MapWorldBuildArgumentException : ArgumentException
{
    /// <summary>Creates a refusal retaining the parameter name.</summary>
    public MapWorldBuildArgumentException(MapDocumentRefusal reason, string message, string? paramName)
        : base(message, paramName) => Reason = reason;
    /// <summary>Creates a refusal retaining its parameter name and original cause.</summary>
    public MapWorldBuildArgumentException(MapDocumentRefusal reason, string message, string? paramName, Exception inner)
        : base(message, paramName, inner) => Reason = reason;

    /// <summary>The rule that refused the world-build identity.</summary>
    public MapDocumentRefusal Reason { get; }
    /// <summary>The nested option field requiring an identity.</summary>
    public string? FieldPath { get; init; }
    /// <summary>The submitted identity text.</summary>
    public string? ActualText { get; init; }
}
