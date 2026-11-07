using System;
using System.Text.Json;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>An embedding refusal preserving the streaming converter's JsonException catch contract.</summary>
public sealed class MapSurfaceEmbeddingJsonException : JsonException
{
    /// <summary>Creates an embedding refusal with its stable reason.</summary>
    public MapSurfaceEmbeddingJsonException(MapDocumentRefusal reason, string message) : base(message) => Reason = reason;
    /// <summary>Creates an embedding refusal retaining its original cause.</summary>
    public MapSurfaceEmbeddingJsonException(MapDocumentRefusal reason, string message, Exception inner) : base(message, inner) => Reason = reason;
    /// <summary>The rule that refused the embedded input.</summary>
    public MapDocumentRefusal Reason { get; }
    /// <summary>The refused field or configured budget, when known.</summary>
    public string? FieldPath { get; init; }
    /// <summary>The exceeded patch, byte or depth budget.</summary>
    public string? Budget { get; init; }
    /// <summary>The patch or container count measured by the guard.</summary>
    public long? ActualCount { get; init; }
    /// <summary>The minimum required byte count known at the failed token.</summary>
    public long? ActualByteCount { get; init; }
    /// <summary>The inclusive limit of the named budget.</summary>
    public long? Limit { get; init; }
    /// <summary>The refused configured integer value.</summary>
    public long? ActualInteger { get; init; }
    /// <summary>The inclusive minimum for the refused configured value.</summary>
    public long? MinimumInteger { get; init; }

    internal static MapSurfaceEmbeddingJsonException FromDocument(MapDocumentException error) => new(error.Reason!.Value, error.Message, error)
    {
        FieldPath = error.FieldPath,
        Budget = error.Budget,
        ActualCount = error.ActualCount,
        ActualByteCount = error.ActualByteCount,
        Limit = error.Limit,
        ActualInteger = error.ActualInteger,
        MinimumInteger = error.MinimumInteger,
    };

    internal MapDocumentException ToDocument(string? sourceName, string message, MapTileCoord? tile,
        string? path, string? resourceId) => new(Reason, message, this)
        {
            SourceName = sourceName,
            Tile = tile,
            Path = path,
            ResourceId = resourceId,
            FieldPath = FieldPath ?? Path,
            LineNumber = LineNumber,
            BytePositionInLine = BytePositionInLine,
            Budget = Budget,
            ActualCount = ActualCount,
            ActualByteCount = ActualByteCount,
            Limit = Limit,
            ActualInteger = ActualInteger,
            MinimumInteger = MinimumInteger,
        };
}
