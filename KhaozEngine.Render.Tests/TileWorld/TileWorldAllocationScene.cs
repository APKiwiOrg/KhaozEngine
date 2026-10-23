using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>Exercises the real retained-cluster renderer without allocating draw recordings.</summary>
internal sealed class TileWorldAllocationScene : ITileWorldScene, ITileWorldPropClusterOwner, IPropClusterRenderBackend
{
    readonly PropClusterRenderer _renderer;
    int _nextMesh;
    public int VisiblePlacements { get; private set; }
    public int VisibleMerged { get; private set; }
    public bool RequiresVisibility { get; private set; }

    public TileWorldAllocationScene() => _renderer = new PropClusterRenderer(this,
        PropHlod.BuildMergedMeshMeasured, static (_, _) => { });
    public ITileWorldPropClusterOwner CreatePropClusterOwner() => this;
    public MeshHandle LoadMesh(GltfMesh mesh) => new(++_nextMesh);
    public void UnloadMesh(MeshHandle handle) { }
    public void DrawMesh(MeshHandle handle, Matrix4x4 world) { }
    public IReadOnlyList<MeshHandle> LoadPropMeshes(IReadOnlyList<GltfMeshPart> parts)
    {
        var handles = new MeshHandle[parts.Count];
        for (int i = 0; i < handles.Length; i++) handles[i] = LoadMesh(parts[i].Mesh);
        return handles;
    }
    public void UnloadPropMeshes(IReadOnlyList<MeshHandle> handles) { }
    public int DrawProps(IReadOnlyList<PropPlacement> placements,
        IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> parts, Vector3 focus, float drawRadius) => placements.Count;

    public PropClusterCpuBuild BuildCpu(PropClusterBuildRequest request) => _renderer.BuildCpu(request);
    public void Apply(PropClusterKey key, PropClusterCpuBuild build) => _renderer.Apply(key, build);
    public void Invalidate(PropClusterKey key) => _renderer.Invalidate(key);
    public void Unload(PropClusterKey key) => _renderer.Unload(key);
    public void Draw(Vector3 focus) => _renderer.Draw(focus);
    public PropClusterDrawCounts DrawWithVisibility(Vector3 focus, IPropClusterVisibility visibility)
    {
        RequiresVisibility = visibility.RequiresVisibility;
        return _renderer.DrawWithVisibility(focus, visibility);
    }
    public bool TryGetDrawState(PropClusterKey key, Vector3 focus, out PropClusterDrawState state) =>
        _renderer.TryGetDrawState(key, focus, out state);
    public void Dispose() => _renderer.Dispose();

    public int DrawProps(IReadOnlyList<PropPlacement> placements, PropLayer layer, Vector3 focus, float dissolveFloor)
    {
        VisiblePlacements += placements.Count;
        return placements.Count;
    }
    public int DrawShadowOnlyProps(IReadOnlyList<PropPlacement> placements, PropLayer layer, Vector3 focus,
        float dissolveFloor) => placements.Count;
    public void DrawMerged(MeshHandle handle, PropLayer layer, float dissolve, bool complement) => VisibleMerged++;
    public void DrawShadowOnlyMerged(MeshHandle handle, PropLayer layer, float dissolve, bool complement) { }
}
