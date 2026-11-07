using System;
using System.Text.Json;

namespace KhaozEngine.MapDoc;

/// <summary>A numeric identity refusal preserving the JsonException catch contract.</summary>
public sealed class MapNumericIdJsonException : JsonException
{
    /// <summary>Creates a refusal with its stable rule and developer-facing message.</summary>
    public MapNumericIdJsonException(MapDocumentRefusal reason, string message) : base(message) => Reason = reason;
    /// <summary>Creates a refusal retaining its original cause.</summary>
    public MapNumericIdJsonException(MapDocumentRefusal reason, string message, Exception inner) : base(message, inner) => Reason = reason;

    /// <summary>The rule that refused the converted value.</summary>
    public MapDocumentRefusal Reason { get; }
    /// <summary>The refused identity field relative to the converted value.</summary>
    public string? FieldPath { get; init; }
    /// <summary>The submitted identity text, when a string was supplied.</summary>
    public string? ActualText { get; init; }
    /// <summary>The submitted integer, when it can be represented.</summary>
    public long? ActualInteger { get; init; }
    /// <summary>The inclusive minimum accepted identity.</summary>
    public long? MinimumInteger { get; init; }
    /// <summary>The inclusive maximum accepted identity.</summary>
    public long? MaximumInteger { get; init; }

    internal MapDocumentException ToDocument(string? sourceName, string message,
        MapTileCoord? tile, string? path, string? resourceId) => new(Reason, message, this)
        {
            SourceName = sourceName,
            Tile = tile,
            Path = path,
            ResourceId = resourceId,
            FieldPath = FieldPath ?? Path,
            LineNumber = LineNumber,
            BytePositionInLine = BytePositionInLine,
            ActualText = ActualText,
            ActualInteger = ActualInteger,
            MinimumInteger = MinimumInteger,
            MaximumInteger = MaximumInteger,
        };
}
