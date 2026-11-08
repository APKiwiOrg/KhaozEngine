using System;
using System.Text.Json;

namespace KhaozEngine.MapDoc;

/// <summary>A sculpt JSON refusal preserving the JsonException catch contract.</summary>
public sealed class MapTerrainOverridesJsonException : JsonException
{
    /// <summary>Creates a refusal with its stable rule and developer-facing message.</summary>
    public MapTerrainOverridesJsonException(MapDocumentRefusal reason, string message) : base(message) => Reason = reason;
    /// <summary>Creates a refusal retaining its original cause.</summary>
    public MapTerrainOverridesJsonException(MapDocumentRefusal reason, string message, Exception inner) : base(message, inner) => Reason = reason;

    /// <summary>The rule that refused the sculpt value.</summary>
    public MapDocumentRefusal Reason { get; }
    /// <summary>The refused field relative to terrainOverrides.</summary>
    public string? FieldPath { get; init; }
    /// <summary>The zero-based tile array position, when known.</summary>
    public int? Index { get; init; }
    /// <summary>The sculpt tile coordinates, only after both coordinates were explicitly read.</summary>
    public MapTileCoord? SculptTile { get; init; }
    /// <summary>The number of deltas observed at the refusal point.</summary>
    public long? ActualCount { get; init; }
    /// <summary>The exact required delta count.</summary>
    public long? ExpectedCount { get; init; }

    internal MapDocumentException ToDocument(string? sourceName, string message,
        MapTileCoord? tile, string? path, string? resourceId) => new(Reason, message, this)
    {
        SourceName = sourceName, Tile = tile, Path = path, ResourceId = resourceId,
        FieldPath = FieldPath ?? Path, LineNumber = LineNumber, BytePositionInLine = BytePositionInLine,
        Index = Index, SculptTile = SculptTile, ActualCount = ActualCount, ExpectedCount = ExpectedCount,
    };
}
