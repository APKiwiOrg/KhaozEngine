using System;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>A navigation policy or tile refusal preserving the ArgumentException catch contract.</summary>
public sealed class MapNavigationArgumentException : ArgumentException
{
    /// <summary>Creates a refusal retaining its parameter name.</summary>
    public MapNavigationArgumentException(MapDocumentRefusal reason, string message, string? paramName = null)
        : base(message, paramName) => Reason = reason;
    /// <summary>Creates a refusal retaining its parameter name and original cause.</summary>
    public MapNavigationArgumentException(MapDocumentRefusal reason, string message, string? paramName, Exception inner)
        : base(message, paramName, inner) => Reason = reason;

    /// <summary>The rule that refused the navigation operation.</summary>
    public MapDocumentRefusal Reason { get; }
    /// <summary>The nested policy or bounds field that failed admission.</summary>
    public string? FieldPath { get; init; }
    /// <summary>The submitted navigation tile, when known.</summary>
    public MapNavTileCoord? Tile { get; init; }
    /// <summary>The preceding tile whose edge must meet the submitted tile.</summary>
    public MapNavTileCoord? OtherTile { get; init; }
    /// <summary>The submitted numeric component or interval endpoint.</summary>
    public double? ActualNumber { get; init; }
    /// <summary>The required value of a component checked for equality.</summary>
    public double? ExpectedNumber { get; init; }
    /// <summary>The inclusive minimum or the submitted lower interval endpoint.</summary>
    public double? MinimumNumber { get; init; }
    /// <summary>The submitted upper interval endpoint.</summary>
    public double? MaximumNumber { get; init; }
    /// <summary>The submitted policy identity, when checked.</summary>
    public string? ActualText { get; init; }
}
