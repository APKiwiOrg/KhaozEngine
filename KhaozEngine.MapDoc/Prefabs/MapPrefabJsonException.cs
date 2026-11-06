using System;
using System.Text.Json;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>A closed prefab JSON refusal that retains the JsonException catch contract.</summary>
public sealed class MapPrefabJsonException : JsonException
{
    /// <summary>Creates a prefab JSON refusal with its stable reason.</summary>
    public MapPrefabJsonException(MapDocumentRefusal reason, string message) : base(message) => Reason = reason;
    /// <summary>Creates a prefab JSON refusal retaining its original cause.</summary>
    public MapPrefabJsonException(MapDocumentRefusal reason, string message, Exception inner) : base(message, inner) => Reason = reason;

    /// <summary>The rule that refused the JSON value.</summary>
    public MapDocumentRefusal Reason { get; }
    /// <summary>The failed member relative to the converted value.</summary>
    public string? FieldPath { get; init; }
    /// <summary>The enumeration whose submitted value is unsupported.</summary>
    public Type? EnumType { get; init; }
    /// <summary>The unsupported text, when the reader received a string.</summary>
    public string? ActualText { get; init; }
    /// <summary>The unsupported enumeration's numeric value, when known.</summary>
    public long? ActualInteger { get; init; }
    /// <summary>The surface named by a refused support binding, when known.</summary>
    public string? SurfaceId { get; init; }
    /// <summary>The space named by a refused support binding, when known.</summary>
    public string? SpaceId { get; init; }

    internal static MapPrefabJsonException FromFinding(MapDocumentFinding finding) => new(finding.Reason, finding.Message)
    {
        FieldPath = finding.FieldPath, EnumType = finding.EnumType, ActualText = finding.ActualText,
        ActualInteger = finding.ActualInteger, SurfaceId = finding.SurfaceId, SpaceId = finding.SpaceId,
    };

    internal MapDocumentException ToDocument(string? sourceName, string message, MapTileCoord? tile,
        string? path, string? resourceId) => new(Reason, message, this)
    {
        SourceName = sourceName, Tile = tile, Path = path, ResourceId = resourceId,
        FieldPath = FieldPath ?? Path, LineNumber = LineNumber, BytePositionInLine = BytePositionInLine,
        EnumType = EnumType, ActualText = ActualText, ActualInteger = ActualInteger, SurfaceId = SurfaceId, SpaceId = SpaceId,
    };
}
