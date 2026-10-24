using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain;

public sealed class NativePropClusterRequest
{
    readonly PropLayer _layer;
    public PropClusterKey Key { get; }
    public long Generation { get; }
    public RectArea Area { get; }
    public PropLayer Layer => Copy(_layer);
    internal PropLayer BorrowLayer => _layer;
    public IReadOnlyList<NativePropInstance> Instances { get; }
    public RenderAnchor Anchor { get; }
    public NativePropClusterRequest(PropClusterKey key, long generation, RectArea area, PropLayer layer,
        IReadOnlyList<NativePropInstance> instances, RenderAnchor anchor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key.LayerId); ArgumentNullException.ThrowIfNull(instances);
        if (instances.Any(i => i is null) || instances.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != instances.Count)
            throw new ArgumentException("A cluster must contain distinct native identities.", nameof(instances));
        Key = key; Generation = generation; Area = area; _layer = Copy(layer);
        Instances = Array.AsReadOnly(instances.ToArray()); Anchor = anchor;
    }
    static PropLayer Copy(PropLayer layer) => layer.HlodSourceMeshes is not { } meshes ? layer : layer.WithHlod(
        new ReadOnlyDictionary<string, GltfMesh>(meshes.ToDictionary(p => p.Key,
            p => new GltfMesh(p.Value.Vertices.ToArray(), p.Value.Indices32.ToArray()), StringComparer.Ordinal)),
        layer.HlodDistance, layer.HlodWeldCell, layer.HlodCrossfadeWidth);
}

/// <summary>Detached immutable CPU geometry. Publication owns its accepted GPU handle separately.</summary>
public sealed class NativePropClusterCpuBuild
{
    readonly GltfMesh _mesh;
    readonly GltfMesh[]? _contributions;
    public NativePropClusterRequest Request { get; }
    public GltfMesh MergedMesh => new(_mesh.Vertices.ToArray(), _mesh.Indices32.ToArray());
    internal GltfMesh BorrowMesh => _mesh;
    internal GltfMesh BorrowContribution(int index) => _contributions?[index]
        ?? throw new InvalidOperationException("Native build did not admit selected contributions.");
    internal IReadOnlyList<GltfMesh> BorrowContributions => _contributions ?? Array.Empty<GltfMesh>();
    public IReadOnlyList<string> RepresentedIds { get; }
    internal NativePropClusterCpuBuild(NativePropClusterRequest request, GltfMesh mesh, GltfMesh[]? contributions = null)
    {
        Request = request; _mesh = mesh; _contributions = contributions;
        RepresentedIds = Array.AsReadOnly(request.Instances.Select(i => i.Id).ToArray());
    }
}
