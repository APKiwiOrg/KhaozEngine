using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Owned copies count independently. Shared layer containers count once within their product.</summary>
internal sealed class MapNativeInventory
{
    internal const long InstanceBytes = 128;
    internal static long WeldPlacementBytes(int count) => checked(64L + (long)Unsafe.SizeOf<PropPlacement>() * count);
    readonly HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);
    internal static long Mesh(GltfMesh mesh, GltfMaterialMaps maps = default) => checked(256L +
        (long)ModelVertex.SizeInBytes * mesh.Vertices.Length + 4L * mesh.Indices32.Length +
        (maps.Albedo?.Rgba.LongLength ?? 0) + (maps.Normal?.Rgba.LongLength ?? 0) + (maps.Roughness?.Rgba.LongLength ?? 0));
    internal long Layer(PropLayer layer) => Layer(layer, true);
    internal long WeldPlacements(NativePropClusterRequest request) =>
        request.WeldPlacements is { } placements && _seen.Add(placements) ? WeldPlacementBytes(placements.Count) : 0;
    internal long Layer(PropLayer layer, bool includeTierPayloads)
    {
        long bytes = 0;
        if (layer.NativePlacements is { } rows && _seen.Add(rows)) bytes += InstanceBytes * rows.Count;
        if (layer.NativeLods is { } lods && _seen.Add(lods))
        {
            bytes += 64L * lods.Count;
            foreach (var tiers in lods.Values)
            {
                if (_seen.Add(tiers)) bytes += System.IntPtr.Size * (long)tiers.Count;
                foreach (var tier in tiers)
                {
                    if (includeTierPayloads && _seen.Add(tier)) bytes += 24;
                    if (includeTierPayloads && _seen.Add(tier.Parts)) bytes += (long)Unsafe.SizeOf<MeshHandle>() * tier.Parts.Count;
                }
            }
        }
        if (layer.LodDistances is { } distances && _seen.Add(distances)) bytes += 4L * distances.Count;
        if (layer.HlodSourceMeshes is { } sources && _seen.Add(sources))
            bytes += 64L * sources.Count + sources.Values.Where(_seen.Add).Sum(m => Mesh(m));
        return checked(bytes);
    }
}
