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
        var bytes = closure.BorrowVerifiedResourceBytes(tier.ResourceId);
        // Selected-asset loading already enforced the caller's decode limits. Measure this additional
        // decode independently and admit its complete transient peak in the active scene preparation.
        var demand = GltfDecodeAdmission.Measure(bytes, new(new(long.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue)));
        if (!demand.Allowed) throw new MapRenderAssetRefusal(MapRenderAssetStatus.CapacityExceeded, "Proxy source decode exceeds representable workspace.");
        using var workspace = MapSceneAdmission.Current!.Workspace();
        workspace.Reserve(demand.PeakBytes);
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
