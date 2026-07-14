using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain
{
    /// <summary>The production <see cref="IChunkSink"/>: turns the streamer's load/unload/re-LOD calls into real
    /// <see cref="Scene3D"/> work. Holds one or more <see cref="PropLayer"/>s - each scatter layer has its own
    /// <see cref="ScatterConfig"/>, mesh set, and draw radius (a dense ground-cover layer at a short radius can
    /// ride alongside the sparse tree layer at a long one), and each companion layer rings its host scatter
    /// layer's placements with foliage. <c>Load</c> builds the chunk mesh at the requested LOD
    /// (<see cref="TerrainChunkBuilder"/>) + scatters every layer for the chunk; <c>ReLod</c> rebuilds the mesh in
    /// place (props are LOD-independent, so they are kept); <c>Unload</c> frees the mesh; <c>Draw</c> queues every
    /// loaded chunk + each layer's in-range props (XZ-culled to that layer's draw radius) every frame. Companions
    /// attach to their host's chunk, so each host emits its companions exactly once (a host lives in one chunk),
    /// even when they spill geometrically into a neighbour. Ships in the package so every game gets streaming for
    /// free.
    /// <para>Disposal: <see cref="Dispose"/> unloads every still-loaded chunk's GPU mesh and clears the ring, so
    /// tearing the sink down while the same <see cref="Scene3D"/> survives (level change / world reload / a teleport
    /// that rebuilds streaming) frees the loaded ring instead of leaking one set of terrain meshes per teardown. The
    /// splat <c>material</c> is caller-owned by default: it is shared across chunks and NOT freed on teardown, so the
    /// caller must <see cref="Scene3D.UnloadSplatMaterial"/> it when done (or reuse it for the rebuilt sink). Pass
    /// <c>ownsMaterial: true</c> to hand ownership to the sink, whose <see cref="Dispose"/> then frees it too. The
    /// material is never disposed per-chunk.</para></summary>
    public sealed class Scene3DChunkSink : IChunkSink, IDisposable
    {
        readonly Scene3D _scene;
        readonly TerrainField _field;
        readonly IReadOnlyList<PropLayer> _layers;
        readonly float _chunkSize;
        readonly Scene3D.SplatMaterialHandle _material;
        readonly IPhysicsWorld? _physics;
        readonly IReadOnlyDictionary<string, PhysicsShape>? _collisionShapes;
        readonly IChunkDynamicsSource? _dynamicsSource;
        readonly bool _collideTerrain;
        readonly bool _ownsMaterial;
        readonly Dictionary<ChunkCoord, ChunkLoad> _loaded = new();
        bool _disposed;

        /// <summary>Multi-layer sink. Each <see cref="PropLayer"/> is a scatter layer or a companion layer; a
        /// companion layer's <see cref="PropLayer.HostLayerIndex"/> must point at a scatter layer in
        /// <paramref name="layers"/>. The splat <paramref name="material"/> is caller-owned unless
        /// <paramref name="ownsMaterial"/> is set (see the class remarks). When <paramref name="physics"/> is
        /// given, each scatter layer's props are added as static bodies on chunk load (using the per-prop-id
        /// shapes in <paramref name="collisionShapes"/>) and removed on unload; null physics = no collision.
        /// When <paramref name="dynamicsSource"/> is given (physics must also be set), the game-supplied source
        /// yields dynamic bodies per chunk that are registered on load and removed on unload (mechanism only:
        /// the engine registers exactly what the source emits, the source decides what spawns where).
        /// <para>Terrain collision (opt-in): when <paramref name="collideTerrain"/> is set (physics must also be
        /// set), each chunk's SURFACE mesh is registered as a static triangle-mesh body on load and removed on
        /// unload, so the terrain surface is part of the unified physics query path (raycasts, capsule sweeps, and
        /// dynamic-body rest all see it) instead of only the analytic <c>TerrainCollision</c> ground-follow
        /// delegate. This is additive: a game that leaves it off keeps the analytic delegate path exactly as
        /// before. See <see cref="TerrainChunkCollision"/> for the surface-only extraction.</para></summary>
        public Scene3DChunkSink(Scene3D scene, TerrainField field, IReadOnlyList<PropLayer> layers,
                                float chunkSize, Scene3D.SplatMaterialHandle material = default, bool ownsMaterial = false,
                                IPhysicsWorld? physics = null,
                                IReadOnlyDictionary<string, PhysicsShape>? collisionShapes = null,
                                IChunkDynamicsSource? dynamicsSource = null,
                                bool collideTerrain = false)
        {
            _scene = scene;
            _field = field ?? throw new ArgumentNullException(nameof(field));
            _layers = layers ?? throw new ArgumentNullException(nameof(layers));
            if (layers.Count == 0)
                throw new ArgumentException("At least one PropLayer is required.", nameof(layers));
            for (int i = 0; i < layers.Count; i++)
            {
                PropLayer l = layers[i];
                if (l.IsCompanion)
                {
                    if (l.HostLayerIndex < 0 || l.HostLayerIndex >= layers.Count)
                        throw new ArgumentException(
                            $"PropLayer {i}: companion HostLayerIndex {l.HostLayerIndex} is out of range.", nameof(layers));
                    if (layers[l.HostLayerIndex].IsCompanion)
                        throw new ArgumentException(
                            $"PropLayer {i}: companion host {l.HostLayerIndex} must be a scatter layer.", nameof(layers));
                }
                else if (l.Scatter == null)
                {
                    throw new ArgumentException($"PropLayer {i} has neither a Scatter nor a Companions config.", nameof(layers));
                }
            }
            _chunkSize = chunkSize;
            _material = material;
            _ownsMaterial = ownsMaterial;
            _physics = physics;
            _collisionShapes = collisionShapes;
            if (dynamicsSource is not null && physics is null)
                throw new ArgumentException("A chunk dynamics source requires a physics world.", nameof(dynamicsSource));
            _dynamicsSource = dynamicsSource;
            if (collideTerrain && physics is null)
                throw new ArgumentException("Terrain collision requires a physics world.", nameof(collideTerrain));
            _collideTerrain = collideTerrain;
        }

        /// <summary>Single-layer sink (back-compat): one scatter config, one mesh set, one draw radius. The splat
        /// <paramref name="material"/> is caller-owned unless <paramref name="ownsMaterial"/> is set (see the class
        /// remarks). Optional <paramref name="physics"/>/<paramref name="collisionShapes"/> add this layer's props
        /// as static bodies on load (see the multi-layer ctor).</summary>
        public Scene3DChunkSink(Scene3D scene, TerrainField field, ScatterConfig scatter,
                                IReadOnlyDictionary<string, MeshHandle> propMeshes, float chunkSize, float propDrawRadius,
                                Scene3D.SplatMaterialHandle material = default, bool ownsMaterial = false,
                                IPhysicsWorld? physics = null,
                                IReadOnlyDictionary<string, PhysicsShape>? collisionShapes = null,
                                IChunkDynamicsSource? dynamicsSource = null,
                                bool collideTerrain = false)
            : this(scene, field,
                   new[]
                   {
                       PropLayer.ScatterLayer(
                           scatter ?? throw new ArgumentNullException(nameof(scatter)),
                           propMeshes ?? throw new ArgumentNullException(nameof(propMeshes)),
                           propDrawRadius),
                   },
                   chunkSize, material, ownsMaterial, physics, collisionShapes, dynamicsSource, collideTerrain)
        {
        }

        /// <summary>The mutable handle for one loaded chunk (the streamer treats it as opaque).</summary>
        public sealed class ChunkLoad
        {
            public MeshHandle Mesh;
            /// <summary>One placement list per layer (scatter or derived companions), index-aligned to the sink's layers.</summary>
            public IReadOnlyList<PropPlacement>[] LayerProps = Array.Empty<IReadOnlyList<PropPlacement>>();
            public int Lod;
            /// <summary>Static body handles added for this chunk's props; empty when no physics world is wired.</summary>
            public List<StaticHandle> Statics = new();
            /// <summary>Dynamic body handles spawned for this chunk; empty when no dynamics source is wired.</summary>
            public List<DynamicBodyHandle> Dynamics = new();
            /// <summary>The chunk's terrain surface collision body, when terrain collision is opted in and the
            /// chunk had surface triangles (<see cref="HasTerrainCollider"/>). Not the props (those are Statics).</summary>
            public StaticHandle TerrainCollider;
            /// <summary>Whether <see cref="TerrainCollider"/> holds a live terrain surface body for this chunk.</summary>
            public bool HasTerrainCollider;

            /// <summary>Back-compat alias: the first layer's placements.</summary>
            public IReadOnlyList<PropPlacement> Props =>
                LayerProps.Length > 0 ? LayerProps[0] : Array.Empty<PropPlacement>();
        }

        /// <summary>The deterministic placements for every layer of a chunk (pure; headless-testable). Scatter
        /// layers first, then companion layers derived from their host layer's placements for THIS chunk.</summary>
        internal IReadOnlyList<PropPlacement>[] ScatterLayersFor(ChunkCoord coord)
        {
            RectArea area = ChunkGrid.AreaOf(coord, _chunkSize);
            var layers = new IReadOnlyList<PropPlacement>[_layers.Count];
            for (int i = 0; i < _layers.Count; i++)
                if (!_layers[i].IsCompanion)
                    layers[i] = PropScatter.Generate(_field, _layers[i].Scatter!, area);
            for (int i = 0; i < _layers.Count; i++)
                if (_layers[i].IsCompanion)
                    layers[i] = PropScatter.GenerateCompanions(_field, layers[_layers[i].HostLayerIndex], _layers[i].Companions!);
            return layers;
        }

        /// <summary>The first layer's placements for a chunk (back-compat for the single-layer path).</summary>
        internal IReadOnlyList<PropPlacement> ScatterFor(ChunkCoord coord) => ScatterLayersFor(coord)[0];

        public object Load(ChunkCoord coord, int lod)
        {
            var mesh = TerrainChunkBuilder.Build(_field, ChunkGrid.RegionOf(coord, _chunkSize), lod);
            var load = new ChunkLoad
            {
                Mesh = _material.IsValid ? _scene.LoadTerrainChunk(mesh, _material) : _scene.LoadTerrainChunk(mesh),
                LayerProps = ScatterLayersFor(coord),
                Lod = lod,
            };
            _loaded[coord] = load;
            if (_physics is not null && _collisionShapes is not null)
                ChunkStatics.AddAll(_physics, _collisionShapes, load.Props, load.Statics);
            if (_physics is not null && _dynamicsSource is not null)
                ChunkDynamics.AddAll(_physics, _dynamicsSource.SpawnsFor(coord), load.Dynamics);
            // Terrain surface collider (opt-in): register the chunk's surface mesh (built above) as a static body so
            // terrain is in the unified physics query path. LOD-dependent geometry, so ReLod rebuilds it.
            if (_collideTerrain && _physics is not null)
                load.HasTerrainCollider = ChunkTerrainCollision.Add(_physics, mesh, out load.TerrainCollider);
            return load;
        }

        public void ReLod(ChunkCoord coord, object handle, int lod)
        {
            var load = (ChunkLoad)handle;
            _scene.UnloadMesh(load.Mesh);
            var mesh = TerrainChunkBuilder.Build(_field, ChunkGrid.RegionOf(coord, _chunkSize), lod);
            load.Mesh = _material.IsValid ? _scene.LoadTerrainChunk(mesh, _material) : _scene.LoadTerrainChunk(mesh);
            load.Lod = lod;
            // Props are LOD-independent; keep load.LayerProps. The terrain surface collider IS LOD-dependent
            // (the mesh resolution changed), so rebuild it: remove the old body, register the new-LOD surface.
            if (_collideTerrain && _physics is not null)
            {
                ChunkTerrainCollision.Remove(_physics, load.HasTerrainCollider, load.TerrainCollider);
                load.HasTerrainCollider = ChunkTerrainCollision.Add(_physics, mesh, out load.TerrainCollider);
            }
        }

        public void Unload(ChunkCoord coord, object handle)
        {
            var load = (ChunkLoad)handle;
            if (_physics is not null)
            {
                ChunkStatics.RemoveAll(_physics, load.Statics);
                ChunkDynamics.RemoveAll(_physics, load.Dynamics);
                ChunkTerrainCollision.Remove(_physics, load.HasTerrainCollider, load.TerrainCollider);
                load.HasTerrainCollider = false;
            }
            _scene.UnloadMesh(load.Mesh);
            _loaded.Remove(coord);
        }

        /// <summary>Draw every loaded chunk mesh and each layer's in-range props (XZ-culled to that layer's draw radius).</summary>
        public void Draw(Vector3 focus)
        {
            foreach (ChunkLoad load in _loaded.Values)
            {
                _scene.DrawTerrainChunk(load.Mesh);
                for (int i = 0; i < _layers.Count; i++)
                {
                    PropLayer layer = _layers[i];
                    // A multi-part layer draws every kit id's sub-meshes as a unit. A single-handle layer draws one
                    // mesh per id (byte-identical to before). Exactly one representation is set per layer.
                    if (layer.PartMeshes is { } partMeshes)
                        _scene.DrawProps(load.LayerProps[i], partMeshes, focus, layer.DrawRadius);
                    else
                        _scene.DrawProps(load.LayerProps[i], layer.Meshes, focus, layer.DrawRadius);
                }
            }
        }

        /// <summary>Free every still-loaded chunk's GPU mesh and clear the ring, so a sink teardown while the same
        /// <see cref="Scene3D"/> survives does not leak the loaded ring. The shared splat material is freed only when
        /// the sink was constructed with <c>ownsMaterial: true</c>; otherwise it is caller-owned and left intact.
        /// Idempotent (a second call is a no-op).</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (ChunkLoad load in _loaded.Values)
            {
                // Remove this chunk's prop static bodies too, so a teardown while the physics world survives
                // (rebuild streaming reusing the same world) frees the ring's colliders instead of leaking them.
                if (_physics is not null)
                {
                    ChunkStatics.RemoveAll(_physics, load.Statics);
                    ChunkDynamics.RemoveAll(_physics, load.Dynamics);
                    ChunkTerrainCollision.Remove(_physics, load.HasTerrainCollider, load.TerrainCollider);
                    load.HasTerrainCollider = false;
                }
                _scene.UnloadMesh(load.Mesh);
            }
            _loaded.Clear();
            if (_ownsMaterial && _material.IsValid)
                _scene.UnloadSplatMaterial(_material);
        }
    }
}
