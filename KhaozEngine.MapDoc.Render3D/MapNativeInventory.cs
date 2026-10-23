using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Owned copies count independently. Shared layer containers count once within their product.</summary>
internal sealed class MapNativeInventory
{
    readonly HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);
    internal static long Mesh(GltfMesh mesh, GltfMaterialMaps maps = default) => checked(256L +
        (long)ModelVertex.SizeInBytes * mesh.Vertices.Length + 4L * mesh.Indices32.Length +
        (maps.Albedo?.Rgba.LongLength ?? 0) + (maps.Normal?.Rgba.LongLength ?? 0) + (maps.Roughness?.Rgba.LongLength ?? 0));
    internal long Layer(PropLayer layer)
    {
        long bytes = 0;
        if (layer.NativePlacements is { } rows && _seen.Add(rows)) bytes += 128L * rows.Count;
        if (layer.NativeLods is { } lods && _seen.Add(lods))
            bytes += lods.Values.Sum(t => 64L + 32L * t.Count + 4L * t.Sum(l => l.Parts.Count));
        if (layer.LodDistances is { } distances && _seen.Add(distances)) bytes += 4L * distances.Count;
        if (layer.HlodSourceMeshes is { } sources && _seen.Add(sources))
            bytes += 64L * sources.Count + sources.Values.Where(_seen.Add).Sum(m => Mesh(m));
        return checked(bytes);
    }
}
