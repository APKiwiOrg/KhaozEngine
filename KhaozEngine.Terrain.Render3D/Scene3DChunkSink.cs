using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
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
        // BuildCpu calls that have entered and not yet returned. Bumped from the build threads, read by UpdateField
        // on the frame thread, so it goes through Interlocked / Volatile rather than a plain int. This is what makes
        // UpdateField's flush precondition enforceable instead of prose (issue #105).
        int _buildsInFlight;
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
        readonly Func<TerrainSplatContext, TerrainSplatWeights>? _splatRule;
        readonly float _snowLine;
        readonly bool _anyHlod;
        /// <summary>The HLOD merge gate, non-null exactly when some layer bakes an HLOD mesh. See
        /// <see cref="HlodBuildGate"/>: it is what keeps a tier re-LOD or a ring change from merging a cluster whose
        /// result the apply would only throw away.</summary>
        readonly HlodBuildGate? _hlodGate;

        /// <summary>The HLOD merge gate, or null when no layer bakes one. Internal (not private) so a headless test
        /// can put a chunk into the applied state a re-LOD would see and then assert what <see cref="BuildCpu"/>
        /// does, without a GPU device to run <see cref="Apply"/> through. Same seam as <see cref="CpuBuild"/>.</summary>
        internal HlodBuildGate? HlodGate => _hlodGate;
        /// <summary>Each placement layer's placements split by chunk coord (index-aligned to the layers, null for
        /// every other layer), or null when no layer carries placements. Built once in the ctor: the list is frozen,
        /// so the split never has to be redone. See <see cref="PlacementBuckets"/>.</summary>
        readonly Dictionary<ChunkCoord, PropPlacement[]>[]? _placementBuckets;
        readonly Dictionary<ChunkCoord, ChunkLoad> _loaded = new();
        // Cumulative HLOD merge counters (see HlodMergeStats). Built is bumped from the background build thread and
        // uploaded from the frame thread, so both go through Interlocked rather than a plain add.
        long _hlodBuilt, _hlodBuiltBytes, _hlodUploaded, _hlodUploadedBytes, _hlodMalformedCornersDropped;
        bool _disposed;

        /// <summary>Cumulative HLOD merge totals for this sink: clusters merged versus clusters an apply actually
        /// consumed, with the byte totals. Always on and allocation-free. A steady difference between the two is
        /// merge work being thrown away, so <see cref="HlodMergeStats.DiscardedBytes"/> is the signal to watch.
        /// Zero on every field when the sink has no HLOD layer.</summary>
        public HlodMergeStats MergeStats => new(
            Interlocked.Read(ref _hlodBuilt), Interlocked.Read(ref _hlodBuiltBytes),
            Interlocked.Read(ref _hlodUploaded), Interlocked.Read(ref _hlodUploadedBytes),
            Interlocked.Read(ref _hlodMalformedCornersDropped));

        /// <summary>Merged-mesh size in bytes: vertices at their interleaved stride plus 4 bytes per 32-bit index.
        /// A null (empty-cluster) mesh is 0, so an empty cluster still counts as a build and an upload of 0 bytes
        /// and the built/uploaded totals stay comparable.</summary>
        static long MeshBytes(GltfMesh? mesh) =>
            mesh is null ? 0L : (long)mesh.Vertices.Length * ModelVertex.SizeInBytes + (long)mesh.Indices32.Length * sizeof(uint);

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
        /// chunks register no scatter, prop colliders, dynamics, or terrain collider - they are render-only.</para>
        /// <para>Splat tuning: <paramref name="snowLine"/> sets the height of the snow transition (default 60), and
        /// <paramref name="splatRule"/> is the optional consumer rule for the per-vertex material mix every chunk
        /// this sink bakes. A null rule is byte-identical to the pre-rule sink - the engine's
        /// own <see cref="TerrainSplatWeights.From"/> weights go straight into the vertex. A world with a SECOND body
        /// of water needs it, because <c>From</c> derives its sand band from the field's single water level, so a lake
        /// edge otherwise bakes as grass running into water. Three constraints, all spelled out on
        /// <see cref="TerrainSplatContext"/> and all load-bearing: the rule must be PURE (each chunk is meshed
        /// independently, per region and LOD, on a background thread, and a meshed chunk is then held until it
        /// re-LODs or unloads, so an impure rule bakes neighbours that disagree at their shared edge and they stay
        /// that way until something rebuilds them), it runs on a HOT PATH (once per vertex of
        /// every streamed chunk), and it is PRESENTATION ONLY (no field, collision, document, or world-identity
        /// impact, so a client may adopt one against a server that has never heard of it). Both settings are fixed for the
        /// sink's lifetime: changing the snow line or mix means a new sink, or a rebuild of
        /// the loaded ring (<see cref="TerrainStreamer.Invalidate(RectArea)"/>) the way a field swap does.</para>
        /// <para>There is no chunk-mesh cache, and this doc used to claim there was one (issue #393, where the wrong
        /// claim sent part of a leak audit down the wrong path). A chunk mesh is rebuilt from the field on every
        /// build, at whatever tier is asked for, and the only thing genuinely reused across a re-LOD is the uploaded
        /// HLOD merged mesh, which <see cref="HlodBuildGate"/> governs.</para></summary>
        public Scene3DChunkSink(Scene3D scene, TerrainField field, IReadOnlyList<PropLayer> layers,
                                float chunkSize, Scene3D.SplatMaterialHandle material = default, bool ownsMaterial = false,
                                IPhysicsWorld? physics = null,
                                IReadOnlyDictionary<string, PhysicsShape>? collisionShapes = null,
                                IChunkDynamicsSource? dynamicsSource = null,
                                bool collideTerrain = false,
                                TerrainLodConfig? lodConfig = null,
                                int collisionLod = 0,
                                Func<TerrainSplatContext, TerrainSplatWeights>? splatRule = null,
                                float snowLine = 60f)
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
                else if (l.Scatter == null && !l.IsPlacement)
                {
                    throw new ArgumentException($"PropLayer {i} has no Scatter config, Companions config, Placements, or PlacementSource.", nameof(layers));
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
            _splatRule = splatRule;
            _snowLine = snowLine;
            // Whether any layer bakes an HLOD merged mesh. When none does, BuildCpu / Apply / Draw skip the HLOD path
            // entirely, so a sink with no HLOD layer is byte-identical to the pre-HLOD sink.
            for (int i = 0; i < snapshot.Length; i++)
                if (snapshot[i].HasHlod) { _anyHlod = true; break; }
            if (_anyHlod) _hlodGate = new HlodBuildGate();
        }

        /// <summary>Single-layer sink (back-compat): one scatter config, one mesh set, one draw radius. The splat
        /// <paramref name="material"/> is caller-owned unless <paramref name="ownsMaterial"/> is set (see the class
        /// remarks). Optional <paramref name="physics"/>/<paramref name="collisionShapes"/> add this layer's props
        /// as static bodies on load (see the multi-layer ctor). <paramref name="snowLine"/> sets the snow transition
        /// height (default 60), and <paramref name="splatRule"/> is the optional consumer rule for the per-vertex
        /// material mix (null = the engine's own weights, byte-identical to the pre-rule sink). See the multi-layer
        /// ctor and <see cref="TerrainSplatContext"/> for the contract.</summary>
        public Scene3DChunkSink(Scene3D scene, TerrainField field, ScatterConfig scatter,
                                IReadOnlyDictionary<string, MeshHandle> propMeshes, float chunkSize, float propDrawRadius,
                                Scene3D.SplatMaterialHandle material = default, bool ownsMaterial = false,
                                IPhysicsWorld? physics = null,
                                IReadOnlyDictionary<string, PhysicsShape>? collisionShapes = null,
                                IChunkDynamicsSource? dynamicsSource = null,
                                bool collideTerrain = false,
                                TerrainLodConfig? lodConfig = null,
                                int collisionLod = 0,
                                Func<TerrainSplatContext, TerrainSplatWeights>? splatRule = null,
                                float snowLine = 60f)
            : this(scene, field,
                   new[]
                   {
                       PropLayer.ScatterLayer(
                           scatter ?? throw new ArgumentNullException(nameof(scatter)),
                           propMeshes ?? throw new ArgumentNullException(nameof(propMeshes)),
                           propDrawRadius),
                   },
                   chunkSize, material, ownsMaterial, physics, collisionShapes, dynamicsSource, collideTerrain, lodConfig, collisionLod,
                   splatRule, snowLine)
        {
        }

        /// <summary>The mutable handle for one loaded chunk (the streamer treats it as opaque).</summary>
        public sealed class ChunkLoad
        {
            public MeshHandle Mesh;
            /// <summary>One placement list per layer (scatter or derived companions), index-aligned to the sink's layers.</summary>
            public IReadOnlyList<PropPlacement>[] LayerProps = Array.Empty<IReadOnlyList<PropPlacement>>();
            public int Lod;
            /// <summary>The world-space square this chunk meshes. Its origin is the chunk's draw translation and its
            /// terrain collider's pose, because chunk vertices are chunk-local (see <see cref="TerrainChunkBuilder"/>).
            /// The sink's own draw could reconstruct it from the coord it iterates, but a consumer holding a
            /// <see cref="ChunkLoad"/> has no coord, so it is carried here.</summary>
            public TerrainChunkRegion Region;
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
        /// A placement layer serves its placements for the chunk at this seam instead of generating (nothing
        /// downstream can tell the three apart), so everything past here is shared with the scatter path: a
        /// frozen-list layer reads its pre-bucketed array, a source-backed layer queries the live source, and a
        /// scatter layer generates.</summary>
        internal IReadOnlyList<PropPlacement>[] ScatterLayersFor(ChunkCoord coord)
        {
            RectArea area = ChunkGrid.AreaOf(coord, _chunkSize);
            var layers = new IReadOnlyList<PropPlacement>[_layers.Count];
            for (int i = 0; i < _layers.Count; i++)
                if (!_layers[i].IsCompanion)
                    layers[i] = _layers[i].PlacementSource is { } source
                        ? Query(source, area)
                        : _layers[i].IsPlacement
                            ? (_placementBuckets![i].TryGetValue(coord, out PropPlacement[]? bucket) ? bucket : Array.Empty<PropPlacement>())
                            : PropScatter.Generate(_field, _layers[i].Scatter!, area);
            for (int i = 0; i < _layers.Count; i++)
                if (_layers[i].IsCompanion)
                    layers[i] = PropScatter.GenerateCompanions(_field, layers[_layers[i].HostLayerIndex], _layers[i].Companions!);
            return layers;
        }

        /// <summary>One live-source layer's placements for a chunk. The list is fresh per build rather than
        /// pooled: it becomes the chunk's own placement list and outlives this call.</summary>
        static IReadOnlyList<PropPlacement> Query(IPlacementSource source, RectArea area)
        {
            var into = new List<PropPlacement>();
            source.PlacementsIn(area, into);
            return into;
        }

        /// <summary>The first layer's placements for a chunk (back-compat for the single-layer path).</summary>
        internal IReadOnlyList<PropPlacement> ScatterFor(ChunkCoord coord) => ScatterLayersFor(coord)[0];

        /// <summary>Swap the field used for every future chunk build (mesh height/splat + prop scatter). This is
        /// the other half of the editor invalidation seam: a chunk already loaded keeps the OLD field's mesh shape
        /// until the caller invalidates or re-LODs it (see <see cref="TerrainStreamer.Invalidate(RectArea)"/>).
        /// This call only changes what a FUTURE build reads. In async mode the caller must flush in-flight builds
        /// (<see cref="TerrainStreamer.FlushPendingBuilds"/>) before swapping, so a build already running against
        /// the old field cannot land after the swap. The map editor runs the streamer in synchronous mode, so this
        /// does not apply there. A FROZEN-LIST placement layer ignores the field by construction: its buckets are
        /// fixed at ctor time, so a swap never changes what it serves. A SOURCE-BACKED placement layer is queried
        /// live per build, so what it serves can change on its own without a field swap.
        /// <para><b>That precondition is enforced, not merely documented</b> (issue #105). A swap while any
        /// <see cref="BuildCpu"/> is EXECUTING throws <see cref="InvalidOperationException"/>, because that build
        /// reads <c>_field</c> at several points (mesh, collision surface, companion scatter) and would otherwise
        /// mesh half a chunk from each field with nothing anywhere to say so. A build that has already RETURNED and
        /// is waiting to apply is not visible here, and is the half <see cref="TerrainStreamer.FlushPendingBuilds"/>
        /// covers, which is why the fix for this exception is to flush rather than to retry.</para></summary>
        public void UpdateField(TerrainField field)
        {
            if (field is null) throw new ArgumentNullException(nameof(field));
            int inFlight = Volatile.Read(ref _buildsInFlight);
            if (inFlight > 0)
                throw new InvalidOperationException(
                    $"UpdateField was called while {inFlight} chunk build(s) are still running against the current field. " +
                    "Flush the streamer first (TerrainStreamer.FlushPendingBuilds), or run it synchronously " +
                    "(StreamerConfig.Synchronous), so no build can mesh one chunk from two different fields.");
            _field = field;
        }

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
            /// <summary>The coarse HLOD merged mesh per layer (index-aligned to the layers), or null when this build
            /// merged nothing: the sink has no HLOD layer, or the apply this build feeds is a tier re-LOD or a ring
            /// change, which keeps the mesh already uploaded (see <see cref="HlodBuildGate"/>). An ENTRY is null when
            /// that layer has no HLOD or the cluster produced no geometry, which is a different thing: the array
            /// being non-null is <see cref="Apply"/>'s signal that this build carries a fresh merge to swap in. Built
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
        /// read only the immutable analytic field, so concurrent chunk builds are safe.
        /// <para>The HLOD cluster merge is built only when the apply will CONSUME it, which is a fresh load or a
        /// rebuild in place, never a tier re-LOD or a ring change (both keep the mesh already on the GPU). See
        /// <see cref="HlodBuildGate"/> for the rule and issue #393 for what it costs to get this wrong: the merge is
        /// multi-megabyte large-object work per chunk, so building it for an apply that discards it is the whole
        /// leak.</para>
        /// <para>The body is wrapped so the sink knows a build is EXECUTING, which is what lets
        /// <see cref="UpdateField"/> refuse a field swap out from under it (issue #105) instead of documenting the
        /// precondition and hoping. The counter is the only thing the wrapper adds: no lock, no allocation, and the
        /// build itself is unchanged.</para></summary>
        public object BuildCpu(ChunkCoord coord, int lod, ChunkRing ring = ChunkRing.Gameplay)
        {
            Interlocked.Increment(ref _buildsInFlight);
            try { return BuildCpuCore(coord, lod, ring); }
            finally { Interlocked.Decrement(ref _buildsInFlight); }
        }

        object BuildCpuCore(ChunkCoord coord, int lod, ChunkRing ring)
        {
            TerrainChunkRegion region = ChunkGrid.RegionOf(coord, _chunkSize);
            bool buildHlod = _hlodGate is not null && _hlodGate.NeedsMerge(coord, lod, ring);
            // Scatter is needed for a gameplay chunk's props AND for an HLOD merge that is actually going to happen
            // (even on a decor chunk, whose merged mesh stands in for the props it never scatters). Compute it once
            // when either applies. A decor re-LOD that merges nothing does not query placements at all.
            IReadOnlyList<PropPlacement>[]? scatter = ring == ChunkRing.Gameplay || buildHlod ? ScatterLayersFor(coord) : null;
            var cpu = new CpuBuild
            {
                // Tier-aware skirt (issue #100). The sink is the one place that knows both the tier table and the
                // chunk size, so it is where the depth stops being a flat 0.3 m and starts following the coarsest
                // cell that can meet this chunk's edge. A direct TerrainChunkBuilder.Build caller still gets the
                // flat default.
                Mesh = TerrainChunkBuilder.Build(_field, region, lod, _lodConfig,
                                                 skirtDepth: _lodConfig.SkirtDepthFor(lod, _chunkSize),
                                                 snowLine: _snowLine, splatRule: _splatRule),
                LayerProps = ring == ChunkRing.Gameplay ? scatter! : EmptyLayers(),
            };
            // Terrain collision surface at the FIXED collision tier, only for a gameplay chunk that opts in. Reuse the
            // render mesh when the tiers coincide (the common near-chunk case), else mesh a second grid off-thread.
            // No splat rule on the second grid: ChunkTerrainCollision reads positions and winding only, so running a
            // per-vertex presentation rule over a mesh whose weights are discarded is pure cost.
            if (ring == ChunkRing.Gameplay && _collideTerrain)
                cpu.CollisionMesh = _collisionLod == lod
                    ? cpu.Mesh
                    : TerrainChunkBuilder.Build(_field, region, _collisionLod, _lodConfig,
                                                skirtDepth: _lodConfig.SkirtDepthFor(_collisionLod, _chunkSize));
            // HLOD merged mesh per layer: merge + weld this cluster's placements into one coarse world-space mesh
            // (deterministic per chunk + field, so a runtime bake at load reproduces). Built for both rings, and only
            // when the apply is going to consume it. A null HlodMeshes is the payload's own signal to Apply that this
            // build carries no fresh merge and the uploaded handles must be kept as they are.
            if (buildHlod)
            {
                var hlod = new GltfMesh?[_layers.Count];
                for (int i = 0; i < _layers.Count; i++)
                {
                    PropLayer layer = _layers[i];
                    if (!layer.HasHlod) continue;
                    GltfMesh m = PropHlod.BuildMergedMeshMeasured(scatter![i], layer.HlodSourceMeshes!,
                                                                 layer.HlodWeldCell,
                                                                 out long malformedCornersDropped);
                    hlod[i] = m.TriangleCount > 0 ? m : null;   // an empty cluster uploads nothing
                    Interlocked.Increment(ref _hlodBuilt);
                    Interlocked.Add(ref _hlodBuiltBytes, MeshBytes(hlod[i]));
                    Interlocked.Add(ref _hlodMalformedCornersDropped, malformedCornersDropped);
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
                    Region = cpu.Mesh.Region,
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
                _hlodGate?.MarkApplied(coord, lod, ring);
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
            relod.Region = cpu.Mesh.Region;   // same square at every tier, refreshed so it can never lag the mesh
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
            // ring re-LOD keeps the cached handle (no GPU churn). Only a rebuild in place (editor Invalidate after a
            // field swap, or a placement source's arrival) rebuilds it, mirroring how the placements + terrain
            // surface refresh only then. The condition is the PAYLOAD, not fieldRebuild: BuildCpu already made this
            // exact call (it is what decides whether to spend the merge at all), so re-deriving it here would be a
            // second copy of the rule that could drift from the one that actually spent the work.
            if (cpu.HlodMeshes is not null)
            {
                UnloadHlod(relod);
                UploadHlod(relod, cpu);
            }
            _hlodGate?.MarkApplied(coord, lod, ring);
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
            {
                if (!_layers[i].HasHlod) continue;
                // Counted per HLOD LAYER, matching how BuildCpu counts, so an empty cluster (no mesh to upload)
                // still balances the built side at 0 bytes instead of showing as permanent waste.
                Interlocked.Increment(ref _hlodUploaded);
                Interlocked.Add(ref _hlodUploadedBytes, MeshBytes(cpu.HlodMeshes[i]));
                if (cpu.HlodMeshes[i] is { } mesh)
                    load.HlodMeshHandles[i] = _scene.LoadMesh(mesh);
            }
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
            // The merged mesh went with it, so the next load of this chunk merges again.
            _hlodGate?.Forget(coord);
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
                _scene.DrawTerrainChunk(load.Mesh, load.Region);

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
                        // one draw, and the band draws both complementary halves. The gates are tightened a little
                        // past the literal 0/1 ends (issue #405, PropHlod.DrawsHlodProps/DrawsHlodMerged): near
                        // either edge the not-yet-fully-gone side's dither has already discarded well over 97 percent
                        // of its fragments, so drawing it still paid full vertex/triangle cost for a sliver of
                        // surviving pixels. The thresholds are provably invisible (see PropHlod's derivation) and
                        // apply to the SAME call that also feeds the shadow-caster registration for that half, so
                        // color and shadow are skipped together with no separate shadow-side gate.
                        float t = PropHlod.CrossfadeAt(chunkDist, layer.HlodDistance, layer.HlodCrossfadeWidth);
                        if (PropHlod.DrawsHlodProps(t))
                            DrawLayerProps(load.LayerProps[i], layer, focus, dissolveFloor: t);
                        if (PropHlod.DrawsHlodMerged(t))
                        {
                            // The merged mesh follows the layer's own casts-shadows policy, so the policy does not
                            // flip at the HLOD swap. Across the crossfade band both halves dissolve in the depth pass
                            // too (props out, merged in), instead of both casting at full strength (issue #287), and
                            // the merged half's SHADOW dither is INVERTED (issue #391) so it keeps exactly what the
                            // fading props' dither discards. Without that the two keep-sets nest rather than
                            // complement (both discard mask < threshold, at t and 1 - t), and the union of the two
                            // shadows bottoms out at half the mask at band centre - the canopy visibly thinning
                            // mid-band. The COLOUR halves stay on the plain rule: they are different geometry in
                            // different places, so they need not complement, and inverting one would change the look.
                            float hlodDissolve = 1f - t;
                            if (hlodDissolve > 0f || !layer.CastsShadows)
                                _scene.Draw(merged, Matrix4x4.Identity, Color.White, Material.None, hlodDissolve, 0f, default,
                                    layer.CastsShadows, invertShadowDissolve: true);
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
        // plus the uniform HLOD crossfade dissolveFloor (0 = unchanged) when the cluster is fading out to its HLOD mesh,
        // the layer's casts-shadows policy (true = unchanged, false keeps the props out of the depth pass), and the
        // layer's blob-radii table (issue #388, null = unchanged, no ShadowBlob registration). This is the ONLY branch
        // that passes BlobRadii through: the merged-HLOD branch in Draw() above calls _scene.Draw directly on the
        // single merged mesh with no per-placement data, so a layer's blobs stop at the HLOD swap automatically.
        void DrawLayerProps(IReadOnlyList<PropPlacement> placements, PropLayer layer, Vector3 focus, float dissolveFloor)
        {
            if (layer.PartMeshes is { } partMeshes)
                _scene.DrawProps(placements, partMeshes, focus, layer.DrawRadius,
                    tint: null, fadeBandWidth: layer.FadeBandWidth, lodParts: layer.LodPartMeshes,
                    lodDistance: layer.LodDistance, dissolveFloor: dissolveFloor, castsShadows: layer.CastsShadows,
                    blobRadii: layer.BlobRadii);
            else
                _scene.DrawProps(placements, layer.Meshes, focus, layer.DrawRadius,
                    tint: null, fadeBandWidth: layer.FadeBandWidth, lodMeshes: layer.LodMeshes,
                    lodDistance: layer.LodDistance, dissolveFloor: dissolveFloor, castsShadows: layer.CastsShadows,
                    blobRadii: layer.BlobRadii);
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
            _hlodGate?.Clear();
            if (_ownsMaterial && _material.IsValid)
                _scene.UnloadSplatMaterial(_material);
        }
    }
}
