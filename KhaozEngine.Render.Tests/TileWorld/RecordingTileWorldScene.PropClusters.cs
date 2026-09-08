using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;

namespace KhaozEngine.Tests.TileWorld;

public sealed partial class RecordingTileWorldScene
{
    readonly HashSet<int> _liveClusterMeshes = new();

    public List<MeshHandle> ClusterMeshLoads { get; } = new();
    public ConcurrentQueue<PropClusterBuildRequest> ClusterBuildRequests { get; } = new();
    public List<MeshHandle> ClusterMeshUnloads { get; } = new();
    public List<(MeshHandle Handle, float Dissolve)> ClusterMergedDraws { get; } = new();
    public List<(IReadOnlyList<PropPlacement> Placements, float DissolveFloor)> ClusterPropDraws { get; } = new();
    public int LiveClusterMeshCount => _liveClusterMeshes.Count;
    public int ClusterDrawPasses { get; private set; }

    public global::KhaozEngine.TileWorld.ITileWorldPropClusterOwner CreatePropClusterOwner() => new RecordingClusterOwner(this);

    sealed class RecordingClusterOwner : global::KhaozEngine.TileWorld.ITileWorldPropClusterOwner
    {
        readonly RecordingTileWorldScene _scene;
        readonly PropClusterRenderer _renderer;

        public RecordingClusterOwner(RecordingTileWorldScene scene)
        {
            _scene = scene;
            _renderer = new PropClusterRenderer(new ClusterBackend(scene), PropHlod.BuildMergedMeshMeasured,
                static (_, _) => { });
        }

        public PropClusterCpuBuild BuildCpu(PropClusterBuildRequest request)
        {
            _scene.ClusterBuildRequests.Enqueue(request);
            return _renderer.BuildCpu(request);
        }
        public void Apply(PropClusterKey key, PropClusterCpuBuild build) => _renderer.Apply(key, build);
        public void Invalidate(PropClusterKey key) => _renderer.Invalidate(key);
        public void Unload(PropClusterKey key) => _renderer.Unload(key);
        public void Draw(Vector3 focus)
        {
            _scene.ClusterDrawPasses++;
            _renderer.Draw(focus);
        }
        public void Dispose() => _renderer.Dispose();
    }

    sealed class ClusterBackend : IPropClusterRenderBackend
    {
        readonly RecordingTileWorldScene _scene;
        public ClusterBackend(RecordingTileWorldScene scene) => _scene = scene;

        public MeshHandle LoadMesh(GltfMesh mesh)
        {
            MeshHandle handle = _scene.LoadMesh(mesh);
            _scene.ClusterMeshLoads.Add(handle);
            _scene._liveClusterMeshes.Add(handle.Index);
            return handle;
        }

        public void UnloadMesh(MeshHandle handle)
        {
            _scene.UnloadMesh(handle);
            if (!_scene._liveClusterMeshes.Remove(handle.Index))
                throw new InvalidOperationException($"cluster mesh {handle.Index} was not live.");
            _scene.ClusterMeshUnloads.Add(handle);
        }

        public void DrawProps(IReadOnlyList<PropPlacement> placements, PropLayer layer, Vector3 focus,
                              float dissolveFloor) =>
            _scene.ClusterPropDraws.Add((placements, dissolveFloor));

        public void DrawMerged(MeshHandle handle, PropLayer layer, float dissolve, bool invertShadowDissolve) =>
            _scene.ClusterMergedDraws.Add((handle, dissolve));
    }
}
