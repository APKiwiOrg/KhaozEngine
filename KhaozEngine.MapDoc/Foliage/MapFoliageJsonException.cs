using System;
using System.Collections.Generic;
using System.Text.Json;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>A structured foliage refusal that retains the JSON converter catch contract.</summary>
public sealed class MapFoliageJsonException : JsonException
{
    IReadOnlyList<int>? _supportedVersions;

    /// <summary>Creates a foliage JSON refusal with its stable reason.</summary>
    public MapFoliageJsonException(MapDocumentRefusal reason, string message) : base(message) => Reason = reason;

    /// <summary>Creates a foliage JSON refusal retaining its original cause.</summary>
    public MapFoliageJsonException(MapDocumentRefusal reason, string message, Exception inner) : base(message, inner) => Reason = reason;

    /// <summary>The rule that refused the foliage JSON section.</summary>
    public MapDocumentRefusal Reason { get; }

    /// <summary>The JSON field in JsonException.Path format, relative to the foliage section.</summary>
    public string? FieldPath { get; init; }
    /// <summary>The foliage layer identity, after it has been read.</summary>
    public string? LayerId { get; init; }
    /// <summary>The zero-based position of the containing or refused JSON array item, when known.</summary>
    public int? Index { get; init; }
    /// <summary>The foliage chunk coordinates, after both coordinates have been read.</summary>
    public MapFoliageChunkKey? FoliageChunk { get; init; }
    /// <summary>The measured or decoded byte count, when the whole value can be measured.</summary>
    public long? ActualByteCount { get; init; }
    /// <summary>The required decoded byte count, when checked.</summary>
    public long? ExpectedByteCount { get; init; }
    /// <summary>The inclusive byte or work limit, when checked.</summary>
    public long? Limit { get; init; }
    /// <summary>The refused payload version, when read as an integer.</summary>
    public int? ActualVersion { get; init; }
    /// <summary>The refused numeric value, when representable as a double.</summary>
    public double? ActualNumber { get; init; }
    /// <summary>The refused numeric value, when representable as a signed 64-bit integer.</summary>
    public long? ActualInteger { get; init; }
    /// <summary>The inclusive minimum integer accepted by the checked field.</summary>
    public long? MinimumInteger { get; init; }
    /// <summary>The inclusive maximum integer accepted by the checked field.</summary>
    public long? MaximumInteger { get; init; }

    /// <summary>The supported payload versions, as an owned read-only snapshot.</summary>
    public IReadOnlyList<int>? SupportedVersions
    {
        get => _supportedVersions;
        init => _supportedVersions = value is null ? null : Array.AsReadOnly<int>([.. value]);
    }

    // The original document refusal stays in the cause chain, so the document reader uses its
    // complete source-copy path even as other areas add identifiers the foliage codec never sets.
    internal static MapFoliageJsonException FromDocument(MapDocumentException inner) =>
        new(inner.Reason ?? MapDocumentRefusal.SchemaMalformed, inner.Message, inner)
        {
            FieldPath = inner.FieldPath,
            LayerId = inner.LayerId,
            Index = inner.Index,
            FoliageChunk = inner.FoliageChunk,
            ActualByteCount = inner.ActualByteCount,
            ExpectedByteCount = inner.ExpectedByteCount,
            Limit = inner.Limit,
            ActualVersion = inner.ActualVersion,
            ActualNumber = inner.ActualNumber,
            ActualInteger = inner.ActualInteger,
            MinimumInteger = inner.MinimumInteger,
            MaximumInteger = inner.MaximumInteger,
            SupportedVersions = inner.SupportedVersions,
        };

    internal MapDocumentException ToDocument(string? sourceName, string message,
        MapTileCoord? tile, string? path, string? resourceId) => new(Reason, message, this)
    {
        SourceName = sourceName, Tile = tile, Path = path, ResourceId = resourceId,
        LineNumber = LineNumber, BytePositionInLine = BytePositionInLine,
        FieldPath = FieldPath ?? Path,
        LayerId = LayerId,
        Index = Index,
        FoliageChunk = FoliageChunk,
        ActualByteCount = ActualByteCount,
        ExpectedByteCount = ExpectedByteCount,
        Limit = Limit,
        ActualVersion = ActualVersion,
        ActualNumber = ActualNumber,
        ActualInteger = ActualInteger,
        MinimumInteger = MinimumInteger,
        MaximumInteger = MaximumInteger,
        SupportedVersions = SupportedVersions,
    };
}
