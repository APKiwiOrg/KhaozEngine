using System;
using System.Text.Json;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>A surface JSON refusal preserving the converter's JsonException catch contract.</summary>
public sealed class MapSurfaceJsonException : JsonException
{
    /// <summary>Creates a surface JSON refusal with its stable reason.</summary>
    public MapSurfaceJsonException(MapDocumentRefusal reason, string message) : base(message) => Reason = reason;

    /// <summary>Creates a surface JSON refusal retaining its original cause.</summary>
    public MapSurfaceJsonException(MapDocumentRefusal reason, string message, Exception inner) : base(message, inner) => Reason = reason;

    /// <summary>The rule that refused the surface JSON value.</summary>
    public MapDocumentRefusal Reason { get; }
    /// <summary>The refused field relative to the converted value, or the lower scalar field.</summary>
    public string? FieldPath { get; init; }
    /// <summary>The numerator of an unreduced authored unit, when known.</summary>
    public int? Numerator { get; init; }
    /// <summary>The denominator of an unreduced authored unit, when known.</summary>
    public int? Denominator { get; init; }
    /// <summary>The submitted integer refused by a scalar guard.</summary>
    public long? ActualInteger { get; init; }
    /// <summary>The inclusive minimum accepted by the scalar guard.</summary>
    public long? MinimumInteger { get; init; }
    /// <summary>The patch surface identity after it has been read or submitted to the writer.</summary>
    public string? SurfaceId { get; init; }
    /// <summary>The submitted noncanonical slot text, when a string was supplied.</summary>
    public string? ActualText { get; init; }
    /// <summary>The repeated patch key, when known.</summary>
    public MapPatchKey? Patch { get; init; }
    /// <summary>The zero-based index of the refused embedded patch.</summary>
    public int? Index { get; init; }

    // Keep the complete scalar refusal as the cause so document wrappers use its source-copy path.
    internal static MapSurfaceJsonException FromDocument(MapDocumentException inner) => new(inner.Reason!.Value, inner.Message, inner)
    {
        FieldPath = inner.FieldPath,
        Numerator = inner.Numerator,
        Denominator = inner.Denominator,
        ActualInteger = inner.ActualInteger,
        MinimumInteger = inner.MinimumInteger,
    };

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
            Numerator = Numerator,
            Denominator = Denominator,
            ActualInteger = ActualInteger,
            MinimumInteger = MinimumInteger,
            SurfaceId = SurfaceId,
            ActualText = ActualText,
            Patch = Patch,
            Index = Index,
        };
}
