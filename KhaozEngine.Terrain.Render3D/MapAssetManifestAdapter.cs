using System;
using System.IO;
using System.Security.Cryptography;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain;

/// <summary>One-way compatibility adaptation from a verified native asset closure to the existing render
/// <see cref="AssetEntry"/>. It selects descriptors and resource references from the closure, never infers a path
/// from an asset or resource ID, and never loads a mesh. Collision, light, selection and all further LOD
/// references stay in the native closure. <see cref="AssetEntry.HeightMeters"/> is a compatibility height from the
/// verified render bounds, not native source-scale loading, which a later round supplies.</summary>
public static class MapAssetManifestAdapter
{
    /// <summary>Builds the entry for <paramref name="assetId"/>. <paramref name="resourceRoot"/> must be the absolute
    /// root the closure was verified against. The mesh and the first declared LOD resolve under it and are
    /// re-hashed against their verified digests, so a stale or missing file throws
    /// <see cref="MapDocumentException"/> instead of reaching the renderer.</summary>
    public static AssetEntry ToAssetEntry(MapAssetClosure closure, string assetId, string resourceRoot)
    {
        ArgumentNullException.ThrowIfNull(closure);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceRoot);
        if (!Path.IsPathFullyQualified(resourceRoot))
            throw new ArgumentException("An explicit absolute resource root is required.", nameof(resourceRoot));
        string root = Path.GetFullPath(resourceRoot);

        MapResolvedAsset asset = closure.GetAsset(assetId);
        string mesh = VerifiedPath(closure, asset.MeshResourceId, root);
        string? lod = asset.LodResourceIds.Count == 0 ? null : VerifiedPath(closure, asset.LodResourceIds[0], root);
        float height = (asset.RenderBounds.Max.Y - asset.RenderBounds.Min.Y) * asset.SourceUnitsToMetres;
        return new AssetEntry(asset.Id, mesh, height, asset.Source, asset.License,
            textured: asset.Textured, category: asset.Category, lodFile: lod);
    }

    static string VerifiedPath(MapAssetClosure closure, string resourceId, string root)
    {
        MapAssetRef reference = closure.GetResource(resourceId).Reference;
        string path = Path.GetFullPath(reference.Path, root);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new MapDocumentException($"Native resource '{resourceId}' is missing or unreadable at '{path}'.", ex);
        }
        string digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!StringComparer.Ordinal.Equals(digest, reference.Sha256))
            throw new MapDocumentException($"Native resource '{resourceId}' at '{path}' no longer matches its verified digest.");
        return path;
    }
}
