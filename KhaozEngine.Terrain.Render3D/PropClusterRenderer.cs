using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain
{
    internal delegate GltfMesh PropClusterMerge(
        IReadOnlyList<PropPlacement> placements,
        IReadOnlyDictionary<string, GltfMesh> sourceMeshes,
        float weldCell,
        out long malformedCornersDropped);

    internal interface IPropClusterRenderBackend
    {
        MeshHandle LoadMesh(GltfMesh mesh);
        void UnloadMesh(MeshHandle handle);
        void DrawProps(IReadOnlyList<PropPlacement> placements, PropLayer layer, Vector3 focus, float dissolveFloor);
        void DrawMerged(MeshHandle handle, PropLayer layer, float dissolve);
    }

    /// <summary>Owns CPU HLOD builds, retained GPU handles, generation replacement, and prop cluster drawing.</summary>
    public sealed class PropClusterRenderer : IDisposable
    {
        const int MaxBuildAttempts = 3;

        readonly object _sync = new();
        readonly IPropClusterRenderBackend _backend;
        readonly PropClusterMerge _merge;
        readonly Action<string, Exception> _logFailure;
        readonly Dictionary<PropClusterKey, Cluster> _clusters = new();
        readonly Dictionary<PropClusterKey, long> _latestGenerations = new();
        readonly HashSet<PropClusterKey> _invalidated = new();
        readonly HashSet<(PropClusterKey Key, long Generation)> _loggedFailures = new();
        long _hlodBuilt;
        long _hlodBuiltBytes;
        long _hlodUploaded;
        long _hlodUploadedBytes;
        long _hlodMalformedCornersDropped;
        bool _disposed;

        /// <summary>Create a prop cluster owner backed by a live scene.</summary>
        public PropClusterRenderer(Scene3D scene)
            : this(new SceneBackend(scene ?? throw new ArgumentNullException(nameof(scene))),
                   PropHlod.BuildMergedMeshMeasured,
                   static (message, error) => Trace.TraceWarning($"{message}: {error}"))
        {
        }

        internal PropClusterRenderer(IPropClusterRenderBackend backend, PropClusterMerge merge,
                                     Action<string, Exception> logFailure)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _merge = merge ?? throw new ArgumentNullException(nameof(merge));
            _logFailure = logFailure ?? throw new ArgumentNullException(nameof(logFailure));
        }

        internal static PropClusterRenderer CreateCpuOnly() => new(
            new CpuOnlyBackend(),
            PropHlod.BuildMergedMeshMeasured,
            static (message, error) => Trace.TraceWarning($"{message}: {error}"));

        /// <summary>Cumulative successful HLOD build and apply totals for this owner.</summary>
        public HlodMergeStats MergeStats => new(
            Interlocked.Read(ref _hlodBuilt), Interlocked.Read(ref _hlodBuiltBytes),
            Interlocked.Read(ref _hlodUploaded), Interlocked.Read(ref _hlodUploadedBytes),
            Interlocked.Read(ref _hlodMalformedCornersDropped));

        /// <summary>Build one cluster generation using CPU data only.</summary>
        public PropClusterCpuBuild BuildCpu(PropClusterBuildRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            ThrowIfDisposed();

            PropLayer layer = request.Layer;
            PropPlacement[] placements = Copy(request.Placements);
            lock (_sync)
            {
                if (_clusters.TryGetValue(request.Key, out Cluster? current) &&
                    current.Generation == request.Generation && !_invalidated.Contains(request.Key))
                    return new PropClusterCpuBuild(request.Key, request.Generation, request.Area, layer,
                        placements, null, reusesCurrent: true, countsHlod: layer.HasHlod, failure: null);
            }

            if (!layer.HasHlod)
                return new PropClusterCpuBuild(request.Key, request.Generation, request.Area, layer,
                    placements, null, reusesCurrent: false, countsHlod: false, failure: null);

            Exception? failure = null;
            for (int attempt = 1; attempt <= MaxBuildAttempts; attempt++)
            {
                try
                {
                    GltfMesh merged = _merge(placements, layer.HlodSourceMeshes!, layer.HlodWeldCell,
                                             out long malformedCornersDropped);
                    GltfMesh? result = merged.TriangleCount > 0 ? merged : null;
                    Interlocked.Increment(ref _hlodBuilt);
                    Interlocked.Add(ref _hlodBuiltBytes, MeshBytes(result));
                    Interlocked.Add(ref _hlodMalformedCornersDropped, malformedCornersDropped);
                    return new PropClusterCpuBuild(request.Key, request.Generation, request.Area, layer,
                        placements, result, reusesCurrent: false, countsHlod: true, failure: null);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }

            lock (_sync)
            {
                if (_loggedFailures.Add((request.Key, request.Generation)))
                    _logFailure($"Prop cluster '{request.Key.LayerId}' generation {request.Generation} failed after {MaxBuildAttempts} attempts", failure!);
            }
            return new PropClusterCpuBuild(request.Key, request.Generation, request.Area, layer,
                placements, null, reusesCurrent: false, countsHlod: true, failure: failure);
        }

        /// <summary>Apply a completed CPU build on the scene thread.</summary>
        public void Apply(PropClusterKey key, PropClusterCpuBuild build)
        {
            ArgumentNullException.ThrowIfNull(build);
            ThrowIfDisposed();
            if (build.Key != key)
                throw new ArgumentException("The build key does not match the apply key.", nameof(build));

            lock (_sync)
            {
                if (_latestGenerations.TryGetValue(key, out long latest) && build.Generation < latest) return;
                if (!_latestGenerations.TryGetValue(key, out latest) || build.Generation > latest)
                    _latestGenerations[key] = build.Generation;
                _clusters.TryGetValue(key, out Cluster? old);
                if (!build.Succeeded)
                {
                    if (old is null)
                        _clusters[key] = new Cluster(build.Generation, build.Area, build.Layer,
                            build.PlacementBatch, handle: null);
                    return;
                }
                if (build.ReusesCurrent)
                {
                    if (old is not null)
                        _clusters[key] = new Cluster(old.Generation, build.Area, build.Layer,
                            build.PlacementBatch, old.Handle);
                    return;
                }

                MeshHandle? fresh = build.MergedMesh is { } mesh ? _backend.LoadMesh(mesh) : null;
                var next = new Cluster(build.Generation, build.Area, build.Layer, build.PlacementBatch, fresh);
                _clusters[key] = next;
                _invalidated.Remove(key);
                _loggedFailures.Remove((key, build.Generation));
                if (build.CountsHlod)
                {
                    Interlocked.Increment(ref _hlodUploaded);
                    Interlocked.Add(ref _hlodUploadedBytes, MeshBytes(build.MergedMesh));
                }
                if (old?.Handle is { } prior) _backend.UnloadMesh(prior);
            }
        }

        /// <summary>Force the next build for a key to replace its accepted generation.</summary>
        public void Invalidate(PropClusterKey key)
        {
            ThrowIfDisposed();
            lock (_sync) _invalidated.Add(key);
        }

        /// <summary>Unload one cluster. Repeated calls are no-ops.</summary>
        public void Unload(PropClusterKey key)
        {
            if (_disposed) return;
            lock (_sync)
            {
                if (_clusters.Remove(key, out Cluster? cluster) && cluster.Handle is { } handle)
                    _backend.UnloadMesh(handle);
                _invalidated.Remove(key);
                _latestGenerations.Remove(key);
                _loggedFailures.RemoveWhere(item => item.Key == key);
            }
        }

        /// <summary>Draw every retained cluster using the layer's existing LOD and HLOD rules.</summary>
        public void Draw(Vector3 focus)
        {
            ThrowIfDisposed();
            lock (_sync)
            {
                foreach (Cluster cluster in _clusters.Values)
                {
                    float centerX = (cluster.Area.MinX + cluster.Area.MaxX) * 0.5f;
                    float centerZ = (cluster.Area.MinZ + cluster.Area.MaxZ) * 0.5f;
                    float dx = centerX - focus.X;
                    float dz = centerZ - focus.Z;
                    float distance = MathF.Sqrt(dx * dx + dz * dz);
                    if (cluster.Handle is { } merged)
                    {
                        float t = PropHlod.CrossfadeAt(distance, cluster.Layer.HlodDistance,
                                                       cluster.Layer.HlodCrossfadeWidth);
                        if (PropHlod.DrawsHlodProps(t))
                            _backend.DrawProps(cluster.Placements, cluster.Layer, focus, t);
                        if (PropHlod.DrawsHlodMerged(t))
                            _backend.DrawMerged(merged, cluster.Layer, 1f - t);
                    }
                    else
                    {
                        _backend.DrawProps(cluster.Placements, cluster.Layer, focus, 0f);
                    }
                }
            }
        }

        internal long GenerationOf(PropClusterKey key)
        {
            lock (_sync) return _clusters.TryGetValue(key, out Cluster? cluster) ? cluster.Generation : -1;
        }

        internal MeshHandle? HandleOf(PropClusterKey key)
        {
            lock (_sync) return _clusters.TryGetValue(key, out Cluster? cluster) ? cluster.Handle : null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_sync)
            {
                foreach (Cluster cluster in _clusters.Values)
                    if (cluster.Handle is { } handle) _backend.UnloadMesh(handle);
                _clusters.Clear();
                _latestGenerations.Clear();
                _invalidated.Clear();
                _loggedFailures.Clear();
            }
        }

        static PropPlacement[] Copy(IReadOnlyList<PropPlacement> source)
        {
            var copy = new PropPlacement[source.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = source[i];
            return copy;
        }

        static long MeshBytes(GltfMesh? mesh) => mesh is null
            ? 0L
            : (long)mesh.Vertices.Length * ModelVertex.SizeInBytes + (long)mesh.Indices32.Length * sizeof(uint);

        void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        sealed class Cluster
        {
            public readonly long Generation;
            public readonly RectArea Area;
            public readonly PropLayer Layer;
            public readonly IReadOnlyList<PropPlacement> Placements;
            public readonly MeshHandle? Handle;

            public Cluster(long generation, RectArea area, PropLayer layer,
                           IReadOnlyList<PropPlacement> placements, MeshHandle? handle)
            {
                Generation = generation;
                Area = area;
                Layer = layer;
                Placements = placements;
                Handle = handle;
            }
        }

        sealed class SceneBackend : IPropClusterRenderBackend
        {
            readonly Scene3D _scene;

            public SceneBackend(Scene3D scene) => _scene = scene;
            public MeshHandle LoadMesh(GltfMesh mesh) => _scene.LoadMesh(mesh);
            public void UnloadMesh(MeshHandle handle) => _scene.UnloadMesh(handle);

            public void DrawProps(IReadOnlyList<PropPlacement> placements, PropLayer layer, Vector3 focus,
                                  float dissolveFloor)
            {
                if (layer.PartMeshes is { } partMeshes)
                    _scene.DrawProps(placements, partMeshes, focus, layer.DrawRadius,
                        tint: null, fadeBandWidth: layer.FadeBandWidth, lodParts: layer.LodPartMeshes,
                        lodDistance: layer.LodDistance, dissolveFloor: dissolveFloor,
                        castsShadows: layer.CastsShadows, blobRadii: layer.BlobRadii);
                else
                    _scene.DrawProps(placements, layer.Meshes, focus, layer.DrawRadius,
                        tint: null, fadeBandWidth: layer.FadeBandWidth, lodMeshes: layer.LodMeshes,
                        lodDistance: layer.LodDistance, dissolveFloor: dissolveFloor,
                        castsShadows: layer.CastsShadows, blobRadii: layer.BlobRadii);
            }

            public void DrawMerged(MeshHandle handle, PropLayer layer, float dissolve)
            {
                if (dissolve > 0f || !layer.CastsShadows)
                    _scene.Draw(handle, Matrix4x4.Identity, Color.White, Material.None, dissolve, 0f, default,
                        layer.CastsShadows, invertShadowDissolve: true);
                else
                    _scene.Draw(handle, Matrix4x4.Identity, Color.White);
            }
        }

        sealed class CpuOnlyBackend : IPropClusterRenderBackend
        {
            static InvalidOperationException NoScene() => new("This prop cluster owner has no Scene3D backend.");
            public MeshHandle LoadMesh(GltfMesh mesh) => throw NoScene();
            public void UnloadMesh(MeshHandle handle) => throw NoScene();
            public void DrawProps(IReadOnlyList<PropPlacement> placements, PropLayer layer, Vector3 focus,
                                  float dissolveFloor) => throw NoScene();
            public void DrawMerged(MeshHandle handle, PropLayer layer, float dissolve) => throw NoScene();
        }
    }
}
