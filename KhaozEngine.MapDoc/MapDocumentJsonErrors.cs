using System;
using System.Text.Json;
using KhaozEngine.MapDoc.Foliage;

namespace KhaozEngine.MapDoc;

/// <summary>Preserves structured JSON causes and the source context known by document readers.</summary>
internal static class MapDocumentJsonErrors
{
    internal static MapDocumentException Wrap(Exception error, string? sourceName, string message,
        MapTileCoord? tile = null, string? path = null, string? resourceId = null)
    {
        MapDocumentException? context = null;
        for (Exception? cause = error.InnerException; cause is not null; cause = cause.InnerException)
            if (cause is MapDocumentException inner)
            {
                if (inner.Reason is not null) return inner.WithSourceName(sourceName, message, tile, path, resourceId);
                context ??= inner;
            }
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
            if (cause is MapFoliageJsonException foliage)
                return foliage.ToDocument(sourceName, message, tile, path, resourceId);
        var json = error as JsonException;
        return new MapDocumentException(MapDocumentRefusal.SchemaMalformed, message, error)
        {
            // A message-only inner refusal has no reason to keep, but the context it carries still applies.
            SourceName = sourceName ?? context?.SourceName, Path = context?.Path ?? path, Tile = context?.Tile ?? tile, ResourceId = context?.ResourceId ?? resourceId,
            FieldPath = context?.FieldPath ?? json?.Path, LineNumber = json?.LineNumber, BytePositionInLine = json?.BytePositionInLine,
        };
    }

    // A value-taking guard keeps null-result admission independent of DTO construction.
    internal static T RequireDeserializedValue<T>(T? value, string message, string sourceName,
        MapTileCoord? tile = null, string? path = null) where T : class =>
        value ?? throw new MapDocumentException(MapDocumentRefusal.SchemaValueRequired, message)
        { SourceName = sourceName, FieldPath = "$", Tile = tile, Path = path };
}
