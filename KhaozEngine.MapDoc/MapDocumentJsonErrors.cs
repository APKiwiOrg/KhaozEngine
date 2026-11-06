using System;
using System.Text.Json;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;

namespace KhaozEngine.MapDoc;

/// <summary>Preserves structured JSON causes and the source context known by document readers.</summary>
internal static class MapDocumentJsonErrors
{
    internal static MapDocumentException Wrap(Exception error, string? sourceName, string message,
        MapTileCoord? tile = null, string? path = null, string? resourceId = null)
    {
        var known = TryWrapKnown(error, sourceName, message, out MapDocumentException? context, tile, path, resourceId);
        if (known is not null) return known;
        var json = error as JsonException;
        return new MapDocumentException(MapDocumentRefusal.SchemaMalformed, message, error)
        {
            // A message-only inner refusal has no reason to keep, but the context it carries still applies.
            SourceName = sourceName ?? context?.SourceName, Path = context?.Path ?? path, Tile = context?.Tile ?? tile, ResourceId = context?.ResourceId ?? resourceId,
            FieldPath = context?.FieldPath ?? json?.Path, LineNumber = json?.LineNumber, BytePositionInLine = json?.BytePositionInLine,
        };
    }

    internal static MapDocumentException? TryWrapKnown(Exception error, string? sourceName, string message,
        out MapDocumentException? context, MapTileCoord? tile = null, string? path = null, string? resourceId = null)
    {
        context = null;
        for (Exception? cause = error.InnerException; cause is not null; cause = cause.InnerException)
            if (cause is MapDocumentException inner)
            {
                if (inner.Reason is not null) return inner.WithSourceName(sourceName, message, tile, path, resourceId);
                context ??= inner;
            }
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
        {
            if (cause is MapTerrainOverridesJsonException sculpt)
                return sculpt.ToDocument(sourceName, message, tile, path, resourceId);
            if (cause is MapFeatureJsonException feature)
                return feature.ToDocument(sourceName, message, tile, path, resourceId);
            if (cause is MapNativeVectorJsonException vector)
                return vector.ToDocument(sourceName, message, tile, path, resourceId);
            if (cause is MapNumericIdJsonException numeric)
                return numeric.ToDocument(sourceName, message, tile, path, resourceId);
            if (cause is MapFoliageJsonException foliage)
                return foliage.ToDocument(sourceName, message, tile, path, resourceId);
            if (cause is MapSurfaceJsonException surface)
                return surface.ToDocument(sourceName, message, tile, path, resourceId);
            if (cause is MapPrefabJsonException prefab)
                return prefab.ToDocument(sourceName, message, tile, path, resourceId);
            if (cause is MapSurfaceEmbeddingJsonException embedding)
                return embedding.ToDocument(sourceName, message, tile, path, resourceId);
        }
        return null;
    }

    // A value-taking guard keeps null-result admission independent of DTO construction.
    internal static T RequireDeserializedValue<T>(T? value, string message, string sourceName,
        MapTileCoord? tile = null, string? path = null) where T : class =>
        value ?? throw new MapDocumentException(MapDocumentRefusal.SchemaValueRequired, message)
        { SourceName = sourceName, FieldPath = "$", Tile = tile, Path = path };
}
