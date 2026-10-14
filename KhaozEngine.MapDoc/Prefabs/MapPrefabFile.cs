using System;
using System.Text.Json;
using KhaozEngine.MapDoc.Assets;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>The closed payload-1 prefab file. Prefabs contain leaf resources and never nested instances.</summary>
public static class MapPrefabFile
{
    public static MapPrefabAsset Read(ReadOnlyMemory<byte> bytes, string resourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        if (bytes.Length > MapPrefabJson.Limits.MaxPayloadBytes) throw new MapDocumentException("Prefab payload byte limit exceeded.");
        try
        {
            using JsonDocument json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            var asset = MapPrefabJson.Read<MapPrefabAsset>(json.RootElement);
            if (!MapAssetVersionIdentity.Matches("prefab", asset.Id, resourceId, bytes.Span)) throw new MapDocumentException("Prefab payload ID does not match its resource ID.");
            MapPrefabValidation.RequireAsset(asset);
            return asset;
        }
        catch (JsonException ex) { throw new MapDocumentException("Invalid prefab payload '" + resourceId + "'.", ex); }
    }

    internal static MapPrefabAsset Read(ReadOnlyMemory<byte> bytes, string resourceId, MapAssetClosure assets)
    {
        var asset = Read(bytes, resourceId);
        MapPrefabValidation.RequireAsset(asset, assets);
        return asset;
    }

    public static byte[] Write(MapPrefabAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        MapPrefabValidation.RequireAsset(asset);
        return MapPrefabJson.Bytes(asset);
    }
}
