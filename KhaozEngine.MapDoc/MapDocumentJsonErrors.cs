using System;
using System.Text.Json;

namespace KhaozEngine.MapDoc;

/// <summary>Preserves structured JSON causes and the source context known by document readers.</summary>
internal static class MapDocumentJsonErrors
{
    internal static MapDocumentException Wrap(Exception error, string? sourceName, string message,
        MapTileCoord? tile = null, string? path = null, string? resourceId = null)
    {
        for (Exception? cause = error.InnerException; cause is not null; cause = cause.InnerException)
            if (cause is MapDocumentException { Reason: not null } known)
                return known.WithSourceName(sourceName, message, tile, path, resourceId);
        var json = error as JsonException;
        return new MapDocumentException(MapDocumentRefusal.SchemaMalformed, message, error)
        {
            SourceName = sourceName, Path = path, Tile = tile, ResourceId = resourceId,
            FieldPath = json?.Path, LineNumber = json?.LineNumber, BytePositionInLine = json?.BytePositionInLine,
        };
    }

    // A value-taking guard keeps null-result admission independent of DTO construction.
    internal static T RequireDeserializedValue<T>(T? value, string message, string sourceName,
        MapTileCoord? tile = null, string? path = null) where T : class =>
        value ?? throw new MapDocumentException(MapDocumentRefusal.SchemaValueRequired, message)
        { SourceName = sourceName, FieldPath = "$", Tile = tile, Path = path };
}
