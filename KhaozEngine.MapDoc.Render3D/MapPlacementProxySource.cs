using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Flattens a previously validated tier from its verified source corners, before placement merging.</summary>
internal static class MapPlacementProxySource
{
    internal static GltfMesh Load(MapRenderAsset asset, MapRenderLod tier, MapAssetClosure closure)
    {
        // Reuse selected loading's verified measurement. Measuring again would allocate JSON and
        // embedded-buffer workspace before the scene or plan could admit it.
        if (tier.DecodePeakBytes <= 0)
            throw new MapRenderAssetRefusal(MapRenderAssetStatus.Invalid, "Proxy source requires a measured selected tier.");
        using var workspace = MapSceneAdmission.Current!.Workspace();
        workspace.Reserve(tier.DecodePeakBytes);
        var bytes = closure.BorrowVerifiedResourceBytes(tier.ResourceId);
        var source = GltfLoader.LoadFlattenedAlbedo(bytes, asset.AssetId + "/" + tier.ResourceId);
        foreach (ref var vertex in source.Vertices.AsSpan()) vertex.Position *= asset.Descriptor.SourceUnitsToMetres;
        return source;
    }

    // The existing no-decimation path keeps material-part corner multiplicity and attributes.
    internal static GltfMesh Concatenate(IReadOnlyList<GltfMeshPart> parts)
    {
        var vertices = new List<ModelVertex>(); var indices = new List<uint>();
        foreach (var part in parts)
        {
            uint start = (uint)vertices.Count;
            var albedo = part.Maps.Albedo is { } image ? GltfLoader.AverageAlbedo(image) : Vector3.One;
            foreach (var v in part.Mesh.Vertices)
            {
                var output = v; output.Color *= new Vector4(albedo, 1); vertices.Add(output);
            }
            indices.AddRange(part.Mesh.Indices32.Select(i => start + i));
        }
        return new(vertices.ToArray(), indices.ToArray());
    }
}
