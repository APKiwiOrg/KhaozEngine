using System;
using System.Text.Json;

namespace KhaozEngine.MapDoc;

/// <summary>A terrain feature refusal preserving the JsonException catch contract.</summary>
public sealed class MapFeatureJsonException : JsonException
{
    /// <summary>Creates a refusal with its stable rule and developer-facing message.</summary>
    public MapFeatureJsonException(MapDocumentRefusal reason, string message) : base(message) => Reason = reason;
    /// <summary>Creates a refusal retaining its original cause.</summary>
    public MapFeatureJsonException(MapDocumentRefusal reason, string message, Exception inner) : base(message, inner) => Reason = reason;

    /// <summary>The rule that refused the converted value.</summary>
    public MapDocumentRefusal Reason { get; }
    /// <summary>The refused field relative to the feature value.</summary>
    public string? FieldPath { get; init; }
    /// <summary>The submitted feature discriminator, when known.</summary>
    public string? ActualText { get; init; }

    internal MapDocumentException ToDocument(string? sourceName, string message,
        MapTileCoord? tile, string? path, string? resourceId) => new(Reason, message, this)
    {
        SourceName = sourceName, Tile = tile, Path = path, ResourceId = resourceId,
        FieldPath = FieldPath ?? Path, LineNumber = LineNumber, BytePositionInLine = BytePositionInLine,
        ActualText = ActualText,
    };
}
