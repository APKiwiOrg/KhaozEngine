using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain
{
    /// <summary>The production <see cref="IChunkSink"/>: turns the streamer's load/unload/re-LOD calls into real
    /// <see cref="Scene3D"/> work. Holds one or more <see cref="PropLayer"/>s - each scatter layer has its own
    /// <see cref="ScatterConfig"/>, mesh set, and draw radius (a dense ground-cover layer at a short radius can
    /// ride alongside the sparse tree layer at a long one), each companion layer rings its host layer's
    /// placements with foliage, and each placement layer (issue #286) serves a frozen author-supplied list
    /// bucketed by chunk at construction instead of generating. <c>Load</c> builds the chunk mesh at the requested LOD
    /// (<see cref="TerrainChunkBuilder"/>) + scatters every layer for the chunk; <c>ReLod</c> rebuilds the mesh in
    /// place and re-adopts the freshly scattered props (byte-identical after a pure LOD change, freshly correct
    /// after a field swap plus invalidate); <c>Unload</c> frees the mesh; <c>Draw</c> queues every
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
    public sealed class Scene3DChunkSink : IAsyncChunkSink, IDisposable
    {
        readonly Scene3D _scene;
        TerrainField _field;
        readonly IReadOnlyList<PropLayer> _layers;
        readonly float _chunkSize;
        readonly Scene3D.SplatMaterialHandle _material;
        readonly IPhysicsWorld? _physics;
        readonly IReadOnlyDictionary<string, PhysicsShape>? _collisionShapes;
        readonly IChunkDynamicsSource? _dynamicsSource;
        readonly bool _collideTerrain;
        readonly bool _ownsMaterial;
        readonly TerrainLodConfig _lodConfig;
        readonly int _collisionLod;
        readonly bool _anyHlod;
        /// <summary>Each placement layer's placements split by chunk coord (index-aligned to the layers, null for
        /// every other layer), or null when no layer carries placements. Built once in the ctor: the list is frozen,
        /// so the split never has to be redone. See <see cref="PlacementBuckets"/>.</summary>
        readonly Dictionary<ChunkCoord, PropPlacement[]>[]? _placementBuckets;
        readonly Dictionary<ChunkCoord, ChunkLoad> _loaded = new();
        bool _disposed;

        /// <summary>Multi-layer sink. Each <see cref="PropLayer"/> is a scatter layer, a companion layer, or a
        /// placement layer (issue #286, a frozen author-supplied list bucketed by chunk here at construction). A
        /// companion layer's <see cref="PropLayer.HostLayerIndex"/> must point at a scatter or placement layer in
        /// <paramref name="layers"/>, either of which yields a per-chunk host list the companions derive from. The
        /// splat <paramref name="material"/> is caller-owned unless
        /// <paramref name="ownsMaterial"/> is set (see the class remarks). When <paramref name="physics"/> is
        /// given, each registering layer's props are added as static bodies on chunk load and removed on unload
        /// (using the per-prop-id shapes in <paramref name="collisionShapes"/>, so a prop id with no shape entry
        /// registers nothing). A placement layer follows its <see cref="PropLayer.RegisterColliders"/> flag at
        /// any index, while any other layer registers only at index 0, so a scatter or companion layer above
        /// index 0 registers no colliders (issue #288). Null physics means no collision. See
        /// <see cref="LayerRegistersColliders"/>.
        /// When <paramref name="dynamicsSource"/> is given (physics must also be set), the game-supplied source
        /// yields dynamic bodies per chunk that are registered on load and removed on unload (mechanism only:
        /// the engine registers exactly what the source emits, the source decides what spawns where).
        /// <para>Terrain collision (opt-in): when <paramref name="collideTerrain"/> is set (physics must also be
        /// set), each chunk's SURFACE mesh is registered as a static triangle-mesh body on load and removed on
        /// unload, so the terrain surface is part of the unified physics query path (raycasts, capsule sweeps, and
        /// dynamic-body rest all see it) instead of only the analytic <c>TerrainCollision</c> ground-follow
        /// delegate. This is additive: a game that leaves it off keeps the analytic delegate path exactly as
        /// before. See <see cref="TerrainChunkCollision"/> for the surface-only extraction.</para>
        /// <para>LOD (data-driven): <paramref name="lodConfig"/> (null = <see cref="TerrainLodConfig.Default"/>) is the
        /// tier table this sink meshes with. It MUST match the one the streamer picks tiers with
        /// (<see cref="StreamerConfig.LodConfig"/>), or a tier index means a different resolution on each side.
        /// <paramref name="collisionLod"/> is the FIXED tier the terrain surface collider is built at when
        /// <paramref name="collideTerrain"/> is on, independent of the render tier: a render re-LOD never rebuilds the
        /// collision body (default 0 = the densest tier, matching the old near-chunk collision resolution). Decor-ring
        /// chunks register no scatter, prop colliders, dynamics, or terrain collider - they are render-only.</para></summary>
        public Scene3DChunkSink(Scene3D scene, TerrainField field, IReadOnlyList<PropLayer> layers,
                                float chunkSize, Scene3D.SplatMaterialHandle material = default, bool ownsMaterial = false,
                                IPhysicsWorld? physics = null,
                                IReadOnlyDictionary<string, PhysicsShape>? collisionShapes = null,
                                IChunkDynamicsSource? dynamicsSource = null,
                                bool collideTerrain = false,
                                TerrainLodConfig? lodConfig = null,
                                int collisionLod = 0)
        {
            _scene = scene;
            _field = field ?? throw new ArgumentNullException(nameof(field));
            if (layers == null) throw new ArgumentNullException(nameof(layers));
            // Snapshot the caller's list: the sink's per-layer state (_placementBuckets, HLOD flags) is
            // fixed-length and built once below, so the layer set is snapshotted here rather than left aliased
            // to the caller's own List<PropLayer>, which the caller could otherwise keep mutating after
            // construction and desync from what was validated and bucketed.
            PropLayer[] snapshot = layers.ToArray();
            _layers = snapshot;
            if (snapshot.Length == 0)
                throw new ArgumentException("At least one PropLayer is required.", nameof(layers));
            for (int i = 0; i < snapshot.Length; i++)
            {
                PropLayer l = snapshot[i];
                if (l.IsCompanion)
                {
                    if (l.HostLayerIndex < 0 || l.HostLayerIndex >= snapshot.Length)
                        throw new ArgumentException(
                            $"PropLayer {i}: companion HostLayerIndex {l.HostLayerIndex} is out of range.", nameof(layers));
                    if (snapshot[l.HostLayerIndex].IsCompanion)
                        throw new ArgumentException(
                            $"PropLayer {i}: companion host {l.HostLayerIndex} must be a scatter or placement layer.",
                            nameof(layers));
                }
                else if (l.Scatter == null && l.Placements == null)
                {
                    throw new ArgumentException($"PropLayer {i} has no Scatter config, Companions config, or Placements.", nameof(layers));
                }
            }
            _chunkSize = chunkSize;
            _placementBuckets = PlacementBuckets.Build(snapshot, chunkSize);
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
            _lodConfig = lodConfig ?? TerrainLodConfig.Default;
            if (collisionLod < 0)
                throw new ArgumentOutOfRangeException(nameof(collisionLod), collisionLod, "Collision LOD tier must be non-negative.");
            _collisionLod = collisionLod;
            // Whether any layer bakes an HLOD merged mesh. When none does, BuildCpu / Apply / Draw skip the HLOD path
            // entirely, so a sink with no HLOD layer is byte-identical to the pre-HLOD sink.
            for (int i = 0; i < snapshot.Length; i++)
                if (snapshot[i].HasHlod) { _anyHlod = true; break; }
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
                                bool collideTerrain = false,
                                TerrainLodConfig? lodConfig = null,
                                int collisionLod = 0)
            : this(scene, field,
                   new[]
                   {
                       PropLayer.ScatterLayer(
                           scatter ?? throw new ArgumentNullException(nameof(scatter)),
                           propMeshes ?? throw new ArgumentNullException(nameof(propMeshes)),
                           propDrawRadius),
                   },
                   chunkSize, material, ownsMaterial, physics, collisionShapes, dynamicsSource, collideTerrain, lodConfig, collisionLod)
        {
        }

        /// <summary>The mutable handle for one loaded chunk (the streamer treats it as opaque).</summary>
        public sealed class ChunkLoad
        {
            public MeshHandle Mesh;
            /// <summary>One placement list per layer (scatter or derived companions), index-aligned to the sink's layers.</summary>
            public IReadOnlyList<PropPlacement>[] LayerProps = Array.Empty<IReadOnlyList<PropPlacement>>();
            public int Lod;
            /// <summary>The residency ring this chunk is built for. A decor chunk carries no scatter or physics.</summary>
            public ChunkRing Ring;
            /// <summary>Static body handles added for this chunk's props; empty when no physics world is wired.</summary>
            public List<StaticHandle> Statics = new();
            /// <summary>Dynamic body handles spawned for this chunk; empty when no dynamics source is wired.</summary>
            public List<DynamicBodyHandle> Dynamics = new();
            /// <summary>The chunk's terrain surface collision body, when terrain collision is opted in and the
            /// chunk had surface triangles (<see cref="HasTerrainCollider"/>). Not the props (those are Statics).</summary>
            public StaticHandle TerrainCollider;
            /// <summary>Whether <see cref="TerrainCollider"/> holds a live terrain surface body for this chunk.</summary>
            public bool HasTerrainCollider;
            /// <summary>The uploaded HLOD merged mesh per layer (index-aligned to the sink's layers), or null when the
            /// sink has no HLOD layer. An entry is null when that layer has no HLOD or the merge produced no geometry.
            /// The coarse mesh for a layer is stable across a tier/ring re-LOD (placements are field-determined) and
            /// rebuilt only on an Invalidate field rebuild, so it is cached here rather than per frame.</summary>
            public MeshHandle?[]? HlodMeshHandles;

            /// <summary>Back-compat alias: the first layer's placements.</summary>
            public IReadOnlyList<PropPlacement> Props =>
                LayerProps.Length > 0 ? LayerProps[0] : Array.Empty<PropPlacement>();
        }

        /// <summary>The deterministic placements for every layer of a chunk (pure, headless-testable). Scatter and
        /// placement layers first, then companion layers derived from their host layer's placements for THIS chunk.
        /// A placement layer serves its pre-bucketed placements for the chunk at this seam instead of generating
        /// (nothing downstream can tell the two apart), so everything past here is shared with the scatter path.</summary>
        internal IReadOnlyList<PropPlacement>[] ScatterLayersFor(ChunkCoord coord)
        {
            RectArea area = ChunkGrid.AreaOf(coord, _chunkSize);
            var layers = new IReadOnlyList<PropPlacement>[_layers.Count];
            for (int i = 0; i < _layers.Count; i++)
                if (!_layers[i].IsCompanion)
                    layers[i] = _layers[i].IsPlacement
                        ? (_placementBuckets![i].TryGetValue(coord, out PropPlacement[]? bucket) ? bucket : Array.Empty<PropPlacement>())
                        : PropScatter.Generate(_field, _layers[i].Scatter!, area);
            for (int i = 0; i < _layers.Count; i++)
                if (_layers[i].IsCompanion)
                    layers[i] = PropScatter.GenerateCompanions(_field, layers[_layers[i].HostLayerIndex], _layers[i].Companions!);
            return layers;
        }

        /// <summary>The first layer's placements for a chunk (back-compat for the single-layer path).</summary>
        internal IReadOnlyList<PropPlacement> ScatterFor(ChunkCoord coord) => ScatterLayersFor(coord)[0];

        /// <summary>Swap the field used for every future chunk build (mesh height/splat + prop scatter). This is
        /// the other half of the editor invalidation seam: a chunk already loaded keeps the OLD field's mesh shape
        /// until the caller invalidates or re-LODs it (see <see cref="TerrainStreamer.Invalidate(RectArea)"/>).
        /// This call only changes what a FUTURE build reads. In async mode the caller must flush in-flight builds
        /// (<see cref="TerrainStreamer.FlushPendingBuilds"/>) before swapping, so a build already running against
        /// the old field cannot land after the swap. The map editor runs the streamer in synchronous mode, so this
        /// does not apply there. A placement layer ignores the field by construction: its buckets are fixed at ctor
        /// time, so a swap never changes what it serves.</summary>
        public void UpdateField(TerrainField field) => _field = field ?? throw new ArgumentNullException(nameof(field));

        /// <summary>The opaque CPU payload <see cref="BuildCpu"/> hands to <see cref="Apply"/>: the pure-CPU mesh and
        /// the per-layer scatter, both built off the analytic field with no GPU device. Everything here is safe to
        /// compute on a worker thread. The GPU upload + physics registration happen later in <see cref="Apply"/>.
        /// Internal (not private) so headless tests can inspect a CPU build's mesh without a GPU device.</summary>
        internal sealed class CpuBuild
        {
            public TerrainChunkMesh Mesh = null!;
            public IReadOnlyList<PropPlacement>[] LayerProps = Array.Empty<IReadOnlyList<PropPlacement>>();
            /// <summary>The surface mesh at the FIXED collision LOD, built only for a gameplay chunk when terrain
            /// collision is opted in (null for decor chunks and when collision is off). Decoupled from
            /// <see cref="Mesh"/>: the collision resolution never follows the render tier, so a re-LOD keeps the same
            /// collision body. Reuses <see cref="Mesh"/> when the collision tier equals the render tier.</summary>
            public TerrainChunkMesh? CollisionMesh;
            /// <summary>The coarse HLOD merged mesh per layer (index-aligned to the layers), or null when the sink has
            /// no HLOD layer. An entry is null when that layer has no HLOD or the cluster produced no geometry. Built
            /// off the analytic scatter (deterministic per chunk + field) for BOTH rings, since a decor chunk renders
            /// the merged mesh in place of the props it never scatters. CPU-only - the GPU upload happens in Apply.</summary>
            public GltfMesh?[]? HlodMeshes;
        }

        /// <summary>Whether layer <paramref name="layerIndex"/>'s props register static collision bodies. A placement
        /// layer follows its own <see cref="PropLayer.RegisterColliders"/> flag (on by default, off via
        /// <c>colliders: false</c> when the game registers that zone's physics itself). Every other layer keeps the
        /// long-standing rule that only layer 0 registers, so a scatter or companion layer above index 0 contributes
        /// nothing (issue #288). Pure, so the rule is headless-testable without a physics world.</summary>
        internal bool LayerRegistersColliders(int layerIndex) =>
            _layers[layerIndex].IsPlacement ? _layers[layerIndex].RegisterColliders : layerIndex == 0;

        // Register this chunk's prop static bodies, one pass over the layers that take colliders (see
        // LayerRegistersColliders). ChunkStatics.AddAll filters per prop id against the shape map, so a layer whose
        // ids have no shapes adds nothing. Callers check _physics / _collisionShapes before calling.
        void AddStatics(ChunkLoad load)
        {
            for (int i = 0; i < _layers.Count; i++)
                if (LayerRegistersColliders(i))
                    ChunkStatics.AddAll(_physics!, _collisionShapes!, load.LayerProps[i], load.Statics);
        }

        MeshHandle UploadMesh(TerrainChunkMesh mesh) =>
            _material.IsValid ? _scene.LoadTerrainChunk(mesh, _material) : _scene.LoadTerrainChunk(mesh);

        /// <summary>An empty per-layer placement array (one empty list per layer), for a decor chunk that carries no
        /// scatter. Index-aligned to the layers so <see cref="Draw"/> iterates it exactly like a gameplay chunk.</summary>
        IReadOnlyList<PropPlacement>[] EmptyLayers()
        {
            var arr = new IReadOnlyList<PropPlacement>[_layers.Count];
            for (int i = 0; i < arr.Length; i++) arr[i] = Array.Empty<PropPlacement>();
            return arr;
        }

        // --- Async seam: BuildCpu (worker thread) + Apply (frame thread) --------------------------------------------

        /// <summary>Build the chunk's mesh (and, for a gameplay chunk, its scatter + fixed-LOD collision surface) with
        /// no GPU access, so the streamer can run it off the frame thread. A decor chunk builds the mesh only - no
        /// scatter, no collision surface. <see cref="TerrainChunkBuilder"/> and <see cref="PropScatter"/> both
        /// read only the immutable analytic field, so concurrent chunk builds are safe.</summary>
        public object BuildCpu(ChunkCoord coord, int lod, ChunkRing ring = ChunkRing.Gameplay)
        {
            TerrainChunkRegion region = ChunkGrid.RegionOf(coord, _chunkSize);
            // Scatter is needed for a gameplay chunk's props AND for any HLOD merge (even on a decor chunk, whose
            // merged mesh stands in for the props it never scatters). Compute it once when either applies.
            IReadOnlyList<PropPlacement>[]? scatter = ring == ChunkRing.Gameplay || _anyHlod ? ScatterLayersFor(coord) : null;
            var cpu = new CpuBuild
            {
                Mesh = TerrainChunkBuilder.Build(_field, region, lod, _lodConfig),
                LayerProps = ring == ChunkRing.Gameplay ? scatter! : EmptyLayers(),
            };
            // Terrain collision surface at the FIXED collision tier, only for a gameplay chunk that opts in. Reuse the
            // render mesh when the tiers coincide (the common near-chunk case), else mesh a second grid off-thread.
            if (ring == ChunkRing.Gameplay && _collideTerrain)
                cpu.CollisionMesh = _collisionLod == lod ? cpu.Mesh : TerrainChunkBuilder.Build(_field, region, _collisionLod, _lodConfig);
            // HLOD merged mesh per layer: merge + weld this cluster's placements into one coarse world-space mesh
            // (deterministic per chunk + field, so a runtime bake at load reproduces). Built for both rings.
            if (_anyHlod)
            {
                var hlod = new GltfMesh?[_layers.Count];
                for (int i = 0; i < _layers.Count; i++)
                {
                    PropLayer layer = _layers[i];
                    if (!layer.HasHlod) continue;
                    GltfMesh m = PropHlod.BuildMergedMesh(scatter![i], layer.HlodSourceMeshes!, layer.HlodWeldCell);
                    hlod[i] = m.TriangleCount > 0 ? m : null;   // an empty cluster uploads nothing
                }
                cpu.HlodMeshes = hlod;
            }
            return cpu;
        }

        /// <summary>Turn a completed CPU build into live GPU + physics state on the frame thread. Fresh load when
        /// <paramref name="existing"/> is null (create the mesh buffers, and for a gameplay chunk register props +
        /// optional terrain collider). Re-LOD otherwise: swap the mesh and adopt the fresh props, but a PURE tier
        /// change within the same gameplay ring keeps the prop static bodies AND the (fixed-resolution) terrain
        /// collider untouched - placements are LOD-independent and the collider is decoupled from the render tier. A
        /// ring change (gameplay &lt;-&gt; decor) gains/drops scatter, colliders, and dynamics; a same-tier same-ring
        /// rebuild (editor Invalidate after a field swap) refreshes them.</summary>
        public object Apply(ChunkCoord coord, int lod, ChunkRing ring, object cpuBuild, object? existing)
        {
            var cpu = (CpuBuild)cpuBuild;
            if (existing is null)
            {
                var load = new ChunkLoad
                {
                    Mesh = UploadMesh(cpu.Mesh),
                    LayerProps = cpu.LayerProps,
                    Lod = lod,
                    Ring = ring,
                };
                _loaded[coord] = load;
                if (ring == ChunkRing.Gameplay)
                {
                    if (_physics is not null && _collisionShapes is not null)
                        AddStatics(load);
                    if (_physics is not null && _dynamicsSource is not null)
                        ChunkDynamics.AddAll(_physics, _dynamicsSource.SpawnsFor(coord), load.Dynamics);
                    if (_collideTerrain && _physics is not null && cpu.CollisionMesh is not null)
                        load.HasTerrainCollider = ChunkTerrainCollision.Add(_physics, cpu.CollisionMesh, out load.TerrainCollider);
                }
                // Decor chunk: render-only for physics. No scatter (LayerProps is empty), no statics/dynamics/terrain
                // collider - but its HLOD merged mesh IS uploaded (a decor chunk shows the far forest as one instance).
                UploadHlod(load, cpu);
                return load;
            }

            var relod = (ChunkLoad)existing;
            int oldLod = relod.Lod;
            ChunkRing oldRing = relod.Ring;
            bool ringChanged = ring != oldRing;
            bool tierChanged = lod != oldLod;
            // Same (tier, ring) with an existing handle only happens through the editor's Invalidate (a field swap at
            // the current tier), where placements + surface may have changed and must be rebuilt.
            bool fieldRebuild = !tierChanged && !ringChanged;

            _scene.UnloadMesh(relod.Mesh);
            relod.Mesh = UploadMesh(cpu.Mesh);
            relod.Lod = lod;
            relod.Ring = ring;
            // Scatter is deterministic per (chunk, field): a pure LOD transition (no field change) reproduces
            // byte-identical placements, so adopting cpu.LayerProps costs nothing extra there, and after a field
            // swap (map-editor carve/paint) plus invalidate it is the ONLY way to see the fresh placements. Keeping
            // the old array left stale props behind after an edit, for example trees still standing in a carved
            // lake. Adopt unconditionally (empty for a decor chunk).
            relod.LayerProps = cpu.LayerProps;

            // Prop static bodies. A pure tier re-LOD inside the gameplay ring keeps them (placements are
            // LOD-independent - the flagged churn fix). A ring change or a field rebuild refreshes them: rebuild from
            // the fresh placements when the chunk is now gameplay, or tear them all down when it is now decor.
            if (_physics is not null && _collisionShapes is not null)
            {
                bool keepStatics = ring == ChunkRing.Gameplay && !ringChanged && !fieldRebuild;
                if (!keepStatics)
                {
                    ChunkStatics.RemoveAll(_physics, relod.Statics);
                    if (ring == ChunkRing.Gameplay)
                        AddStatics(relod);
                }
            }

            // Dynamics only change on a ring transition (they are game-spawned bodies with their own physics state,
            // so a tier change or a field edit must not reset them): spawn on decor -> gameplay, drop on the reverse.
            if (_physics is not null && _dynamicsSource is not null && ringChanged)
            {
                if (ring == ChunkRing.Gameplay)
                    ChunkDynamics.AddAll(_physics, _dynamicsSource.SpawnsFor(coord), relod.Dynamics);
                else
                    ChunkDynamics.RemoveAll(_physics, relod.Dynamics);
            }

            // Terrain surface collider is at a FIXED resolution, decoupled from the render tier. A pure tier re-LOD
            // inside the gameplay ring leaves it alone (the decouple: no triangle-mesh rebuild on a tier crossing). A
            // ring change or a field rebuild rebuilds it: re-add from the fresh collision surface when now gameplay,
            // drop it when now decor.
            if (_collideTerrain && _physics is not null)
            {
                bool keepTerrainCollider = ring == ChunkRing.Gameplay && !ringChanged && !fieldRebuild;
                if (!keepTerrainCollider)
                {
                    ChunkTerrainCollision.Remove(_physics, relod.HasTerrainCollider, relod.TerrainCollider);
                    relod.HasTerrainCollider = false;
                    if (ring == ChunkRing.Gameplay && cpu.CollisionMesh is not null)
                        relod.HasTerrainCollider = ChunkTerrainCollision.Add(_physics, cpu.CollisionMesh, out relod.TerrainCollider);
                }
            }

            // HLOD merged mesh: the coarse geometry is field-determined and tier/ring-independent, so a pure tier or
            // ring re-LOD keeps the cached handle (no GPU churn). Only a field rebuild (editor Invalidate after a
            // field swap) rebuilds it, mirroring how the placements + terrain surface refresh only then.
            if (_anyHlod && fieldRebuild)
            {
                UnloadHlod(relod);
                UploadHlod(relod, cpu);
            }
            return relod;
        }

        // Upload each layer's freshly built HLOD merged mesh into its own MeshHandle (the vertex-colour untextured
        // path, one instanced draw). A no-op when the sink has no HLOD layer. Called on a fresh load and on a field
        // rebuild; the caller unloads the previous handles first on a rebuild.
        void UploadHlod(ChunkLoad load, CpuBuild cpu)
        {
            if (!_anyHlod || cpu.HlodMeshes is null) return;
            load.HlodMeshHandles = new MeshHandle?[_layers.Count];
            for (int i = 0; i < _layers.Count; i++)
                if (cpu.HlodMeshes[i] is { } mesh)
                    load.HlodMeshHandles[i] = _scene.LoadMesh(mesh);
        }

        // Free a chunk's uploaded HLOD meshes and clear the handle array. Idempotent (null handles / already cleared).
        void UnloadHlod(ChunkLoad load)
        {
            if (load.HlodMeshHandles is null) return;
            for (int i = 0; i < load.HlodMeshHandles.Length; i++)
                if (load.HlodMeshHandles[i] is { } handle) _scene.UnloadMesh(handle);
            load.HlodMeshHandles = null;
        }

        public object Load(ChunkCoord coord, int lod, ChunkRing ring = ChunkRing.Gameplay) =>
            Apply(coord, lod, ring, BuildCpu(coord, lod, ring), existing: null);

        public void ReLod(ChunkCoord coord, object handle, int lod, ChunkRing ring = ChunkRing.Gameplay) =>
            Apply(coord, lod, ring, BuildCpu(coord, lod, ring), handle);

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
            UnloadHlod(load);
            _scene.UnloadMesh(load.Mesh);
            _loaded.Remove(coord);
        }

        /// <summary>Draw every loaded chunk mesh and each layer's in-range props (XZ-culled to that layer's draw
        /// radius). A layer with HLOD swaps a chunk cluster's individual props for its merged coarse mesh past
        /// <see cref="PropLayer.HlodDistance"/>, crossfading the two across
        /// <see cref="PropLayer.HlodCrossfadeWidth"/> by the chunk-centre distance (both dissolves via the 14.5.0
        /// rigid primitive, deterministic by distance).</summary>
        public void Draw(Vector3 focus)
        {
            foreach (KeyValuePair<ChunkCoord, ChunkLoad> kv in _loaded)
            {
                ChunkLoad load = kv.Value;
                _scene.DrawTerrainChunk(load.Mesh);

                // Chunk-centre horizontal distance drives the per-cluster HLOD crossfade (one merged mesh per chunk,
                // so the swap is decided per chunk, not per placement). Only computed when a layer needs it.
                Vector2 center = ChunkGrid.CenterOf(kv.Key, _chunkSize);
                float cdx = center.X - focus.X, cdz = center.Y - focus.Z;
                float chunkDist = MathF.Sqrt(cdx * cdx + cdz * cdz);

                for (int i = 0; i < _layers.Count; i++)
                {
                    PropLayer layer = _layers[i];
                    MeshHandle? hlodHandle = layer.HasHlod && load.HlodMeshHandles is { } hh ? hh[i] : null;
                    if (hlodHandle is { } merged)
                    {
                        // Crossfade: props dissolve out (floor = t) up to the far edge, the merged mesh dissolves in
                        // (1 - t) from the near edge. Skip whichever side is fully gone so the common near/far case is
                        // one draw, and the band draws both complementary halves.
                        float t = PropHlod.CrossfadeAt(chunkDist, layer.HlodDistance, layer.HlodCrossfadeWidth);
                        if (t < 1f)
                            DrawLayerProps(load.LayerProps[i], layer, focus, dissolveFloor: t);
                        if (t > 0f)
                        {
                            float hlodDissolve = 1f - t;
                            if (hlodDissolve > 0f)
                                _scene.Draw(merged, Matrix4x4.Identity, Color.White, Material.None, hlodDissolve, 0f, default);
                            else
                                _scene.Draw(merged, Matrix4x4.Identity, Color.White);
                        }
                    }
                    else
                    {
                        DrawLayerProps(load.LayerProps[i], layer, focus, dissolveFloor: 0f);
                    }
                }
            }
        }

        // Draw one layer's in-range props. A multi-part layer draws every kit id's sub-meshes as a unit; a single-handle
        // layer draws one mesh per id (byte-identical to before). Exactly one representation is set per layer. Each
        // layer's fade band + far LOD variants (both defaulting to the old hard-cut, full-mesh behaviour) ride through,
        // plus the uniform HLOD crossfade dissolveFloor (0 = unchanged) when the cluster is fading out to its HLOD mesh.
        void DrawLayerProps(IReadOnlyList<PropPlacement> placements, PropLayer layer, Vector3 focus, float dissolveFloor)
        {
            if (layer.PartMeshes is { } partMeshes)
                _scene.DrawProps(placements, partMeshes, focus, layer.DrawRadius,
                    tint: null, fadeBandWidth: layer.FadeBandWidth, lodParts: layer.LodPartMeshes,
                    lodDistance: layer.LodDistance, dissolveFloor: dissolveFloor);
            else
                _scene.DrawProps(placements, layer.Meshes, focus, layer.DrawRadius,
                    tint: null, fadeBandWidth: layer.FadeBandWidth, lodMeshes: layer.LodMeshes,
                    lodDistance: layer.LodDistance, dissolveFloor: dissolveFloor);
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
                UnloadHlod(load);
                _scene.UnloadMesh(load.Mesh);
            }
            _loaded.Clear();
            if (_ownsMaterial && _material.IsValid)
                _scene.UnloadSplatMaterial(_material);
        }
    }
}
