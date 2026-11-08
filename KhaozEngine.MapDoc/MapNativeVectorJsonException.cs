using System;
using System.Text.Json;

namespace KhaozEngine.MapDoc;

/// <summary>A native vector refusal preserving the JsonException catch contract.</summary>
public sealed class MapNativeVectorJsonException : JsonException
{
    /// <summary>Creates a refusal with its stable rule and developer-facing message.</summary>
    public MapNativeVectorJsonException(MapDocumentRefusal reason, string message) : base(message) => Reason = reason;
    /// <summary>Creates a refusal retaining its original cause.</summary>
    public MapNativeVectorJsonException(MapDocumentRefusal reason, string message, Exception inner) : base(message, inner) => Reason = reason;

    /// <summary>The rule that refused the converted value.</summary>
    public MapDocumentRefusal Reason { get; }
    /// <summary>The refused component relative to the vector, or the vector root.</summary>
    public string? FieldPath { get; init; }
    /// <summary>The nonfinite component, when a number was parsed or submitted.</summary>
    public double? ActualNumber { get; init; }

    internal MapDocumentException ToDocument(string? sourceName, string message,
        MapTileCoord? tile, string? path, string? resourceId) => new(Reason, message, this)
    {
        SourceName = sourceName, Tile = tile, Path = path, ResourceId = resourceId,
        FieldPath = FieldPath ?? Path, LineNumber = LineNumber, BytePositionInLine = BytePositionInLine,
        ActualNumber = ActualNumber,
    };
}
