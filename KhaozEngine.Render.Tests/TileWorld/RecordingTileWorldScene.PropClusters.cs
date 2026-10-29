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
    public List<(IReadOnlyList<PropPlacement> Placements, float DissolveFloor)> ClusterShadowPropDraws { get; } = new();
    public List<(IReadOnlyList<PropPlacement> Placements, float DissolveFloor)> ClusterColourOnlyPropDraws { get; } = new();
    public List<(MeshHandle Handle, float Dissolve, bool Complement)> ClusterShadowMergedDraws { get; } = new();
    public int LiveClusterMeshCount => _liveClusterMeshes.Count;
    public int ClusterDrawPasses { get; private set; }
    public bool FailClusterBuilds { get; set; }

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
            PropClusterCpuBuild build = _renderer.BuildCpu(request);
            return !_scene.FailClusterBuilds ? build : new PropClusterCpuBuild(build.Key, build.Generation,
                build.Area, build.Layer, build.PlacementBatch, null, false, true,
                new InvalidOperationException("fixture rebuild failure"), build.Epoch);
        }
        public void Apply(PropClusterKey key, PropClusterCpuBuild build) => _renderer.Apply(key, build);
        public void Invalidate(PropClusterKey key) => _renderer.Invalidate(key);
        public void Unload(PropClusterKey key) => _renderer.Unload(key);
        public void Draw(Vector3 focus)
        {
            _scene.ClusterDrawPasses++;
            _renderer.Draw(focus);
        }
        public PropClusterDrawCounts DrawWithVisibility(Vector3 focus, IPropClusterVisibility visibility)
        {
            _scene.ClusterDrawPasses++;
            return _renderer.DrawWithVisibility(focus, visibility);
        }
        public bool TryGetDrawState(PropClusterKey key, Vector3 focus, out PropClusterDrawState state) =>
            _renderer.TryGetDrawState(key, focus, out state);
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

        public int DrawProps(IReadOnlyList<PropPlacement> placements, PropLayer layer, Vector3 focus,
            float dissolveFloor)
        {
            var submitted = InRange(placements, layer, focus);
            if (submitted.Count > 0) _scene.ClusterPropDraws.Add((submitted, dissolveFloor));
            return submitted.Count;
        }

        public int DrawShadowOnlyProps(IReadOnlyList<PropPlacement> placements, PropLayer layer, Vector3 focus,
            float dissolveFloor)
        {
            var submitted = InRange(placements, layer, focus);
            if (submitted.Count > 0) _scene.ClusterShadowPropDraws.Add((submitted, dissolveFloor));
            return submitted.Count;
        }

        public int DrawColourOnlyProps(IReadOnlyList<PropPlacement> placements, PropLayer layer, Vector3 focus,
            float dissolveFloor)
        {
            var submitted = InRange(placements, layer, focus);
            if (submitted.Count > 0) _scene.ClusterColourOnlyPropDraws.Add((submitted, dissolveFloor));
            return submitted.Count;
        }

        static List<PropPlacement> InRange(IReadOnlyList<PropPlacement> placements, PropLayer layer, Vector3 focus)
        {
            var submitted = new List<PropPlacement>();
            foreach (var placement in placements)
            {
                float dx = placement.X - focus.X, dz = placement.Z - focus.Z;
                if (dx * dx + dz * dz <= layer.DrawRadius * layer.DrawRadius) submitted.Add(placement);
            }
            return submitted;
        }

        public void DrawShadowOnlyMerged(MeshHandle handle, PropLayer layer, float dissolve, bool invertShadowDissolve) =>
            _scene.ClusterShadowMergedDraws.Add((handle, dissolve, invertShadowDissolve));

        public void DrawMerged(MeshHandle handle, PropLayer layer, float dissolve, bool invertShadowDissolve) =>
            _scene.ClusterMergedDraws.Add((handle, dissolve));
    }
}
