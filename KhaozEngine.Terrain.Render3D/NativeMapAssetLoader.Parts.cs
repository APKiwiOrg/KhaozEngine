using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain;

public static partial class NativeMapAssetLoader
{
    /// <summary>Loads a declared mesh tier with strict image admission, preserving origin, parts and units.</summary>
    public static IReadOnlyList<GltfMeshPart> LoadParts(MapResolvedAsset asset, MapAssetClosure closure,
        GltfDecodeBudget budget, string? lodResourceId = null)
    {
        RequireDescriptor(asset, closure); ArgumentNullException.ThrowIfNull(budget);
        string id = lodResourceId ?? asset.MeshResourceId;
        if (lodResourceId is not null && !asset.LodResourceIds.Contains(lodResourceId, StringComparer.Ordinal))
            throw new MapDocumentException("LOD is not declared by asset '" + asset.Id + "'.");
        MapResolvedResource resource = closure.GetResource(id);
        if (resource.Kind != (lodResourceId is null ? MapResourceKind.Mesh : MapResourceKind.Lod))
            throw new MapDocumentException("Native mesh resource kind differs from its declaration.");
        if (!budget.TryReserve(0, resource.ByteLength))
            throw new GltfDecodeRefusal(GltfDecodeStatus.CapacityExceeded, "Encoded native mesh copy exceeds the remaining allowance.");
        long owned = 0;
        long scaling = 0;
        try
        {
            ReadOnlyMemory<byte> bytes = resource.Bytes;
            var measured = GltfDecodeAdmission.Measure(bytes, budget);
            var parts = GltfLoader.LoadPartsWithMaterialsStrict(bytes, asset.Id + "/" + id, budget);
            owned = measured.OwnedBytes;
            long copyBytes = parts.Sum(part => checked(24L + part.Mesh.Vertices.LongLength * ModelVertex.SizeInBytes));
            if (!budget.TryReserve(0, copyBytes)) throw new GltfDecodeRefusal(GltfDecodeStatus.CapacityExceeded, "Scaled native vertices exceed the remaining allowance.");
            scaling = copyBytes;
            var scaled = new GltfMeshPart[parts.Count];
            for (int p = 0; p < scaled.Length; p++)
            {
                var part = parts[p];
                var vertices = part.Mesh.Vertices.ToArray();
                for (int v = 0; v < vertices.Length; v++) vertices[v].Position *= asset.SourceUnitsToMetres;
                scaled[p] = new(new(vertices, part.Mesh.Indices32), part.Maps);
            }
            owned = 0;
            return Array.AsReadOnly(scaled);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        { throw new MapDocumentException("Native asset '" + asset.Id + "' resource '" + id + "' cannot be loaded: " + ex.Message, ex); }
        finally
        {
            budget.ReleaseTemporary(resource.ByteLength + scaling);
            if (owned != 0) budget.ReleaseOwned(owned);
        }
    }

    static void RequireDescriptor(MapResolvedAsset asset, MapAssetClosure closure)
    {
        ArgumentNullException.ThrowIfNull(asset); ArgumentNullException.ThrowIfNull(closure);
        if (!ReferenceEquals(closure.GetAsset(asset.Id), asset))
            throw new MapDocumentException($"Native asset '{asset.Id}' is not the descriptor of the supplied closure.");
    }
}
