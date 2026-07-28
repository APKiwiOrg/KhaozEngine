using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain
{
    /// <summary>One prop layer for the multi-layer <see cref="Scene3DChunkSink"/>: a <em>scatter</em> layer
    /// (an independent <see cref="ScatterConfig"/>, e.g. sparse trees at a long draw radius), a <em>companion</em>
    /// layer (a <see cref="CompanionConfig"/> + the index of its host scatter layer, e.g. understory foliage rung
    /// around the trees at a short draw radius), or a <em>placement</em> layer (a frozen, author-supplied list of
    /// exact <see cref="PropPlacement"/>s, issue #286, e.g. a frozen zone's authored decor with no procedural
    /// generation). Each layer carries its own mesh set and draw radius - the short radius on a dense layer is what
    /// keeps it affordable, and each layer sets its own dissolve fade band (<see cref="FadeBandWidth"/>) and optional
    /// far LOD variants (<see cref="LodMeshes"/> / <see cref="LodPartMeshes"/> at <see cref="LodDistance"/>). Build
    /// one with
    /// <see cref="ScatterLayer(ScatterConfig, IReadOnlyDictionary{string, MeshHandle}, float, float, IReadOnlyDictionary{string, MeshHandle}, float, bool, IReadOnlyDictionary{string, float})"/>,
    /// <see cref="CompanionLayer(int, CompanionConfig, IReadOnlyDictionary{string, MeshHandle}, float, float, IReadOnlyDictionary{string, MeshHandle}, float, bool, IReadOnlyDictionary{string, float})"/>, or
    /// <see cref="PlacementLayer(IReadOnlyList{PropPlacement}, IReadOnlyDictionary{string, MeshHandle}, float, float, IReadOnlyDictionary{string, MeshHandle}, float, bool, bool, IReadOnlyDictionary{string, float})"/>
    /// (or their multi-part overloads).</summary>
    public readonly struct PropLayer
    {
        public ScatterConfig? Scatter { get; }
        public CompanionConfig? Companions { get; }
        /// <summary>For a companion layer, the index (into the sink's layer list) of the scatter layer whose
        /// placements are the hosts. Unused (-1) for a scatter layer or a placement layer.</summary>
        public int HostLayerIndex { get; }
        /// <summary>Frozen, author-supplied placements for a placement layer (issue #286): used verbatim instead of
        /// runtime procedural generation, e.g. a frozen zone's authored decor. Null for a scatter or companion layer,
        /// which generate their placements procedurally instead. When set, <see cref="Scene3DChunkSink"/> buckets
        /// these placements by chunk coordinate once at construction and streams them exactly like any other
        /// layer kind.</summary>
        public IReadOnlyList<PropPlacement>? Placements { get; }
        /// <summary>A LIVE placement source for a placement layer, queried at EVERY chunk build instead of
        /// bucketed once at construction. Null for a frozen-list placement layer and for every scatter or
        /// companion layer. This is what lets content that arrives after the sink was built (a streamed document
        /// tile) reach the renderer at all: <see cref="Placements"/> is split into per-chunk buckets once, so a
        /// placement added later would never draw. Exactly one of <see cref="Placements"/> and this is set on a
        /// placement layer. See <see cref="IPlacementSource"/> for the build-thread contract.</summary>
        public IPlacementSource? PlacementSource { get; }
        /// <summary>Whether this layer's props register physics colliders in the sink. True for every scatter and
        /// companion layer (unchanged behaviour) and true by default for a placement layer. A placement layer opts
        /// out via <c>colliders: false</c> on its factory when the placements are render-only and the consuming
        /// game registers their physics separately (issue #286).</summary>
        public bool RegisterColliders { get; }
        /// <summary>Whether this layer's props write into the key light's shadow depth pass (issue #287). True by
        /// default on every factory, so the whole pre-flag behaviour is unchanged. Set it false for a layer whose
        /// cast shadows cost more than they read - a dense short-radius ground-cover or understory layer, where
        /// hundreds of small casters pop on the draw-radius circle - and its props still draw and still RECEIVE
        /// shadows, they just stop casting. Applies to the layer's individual props AND to its merged HLOD mesh, so
        /// the policy does not change at the HLOD swap.</summary>
        public bool CastsShadows { get; }
        /// <summary>Optional per-kit blob-shadow radius table (issue #388): the ground-footprint radius (at
        /// placement <see cref="PropPlacement.Scale"/> 1) a kit contributes at <see cref="ShadowMode.Blob"/>. Null
        /// (the default on every factory) means this layer never registers a <see cref="ShadowBlob"/>, so the whole
        /// seam is inert until a caller opts a kit in - byte-identical to before. A kit id absent from a non-null
        /// table also gets no blob (per-kit opt-in, mirroring <see cref="LodMeshes"/>/<see cref="HlodSourceMeshes"/>).
        /// Consulted by <see cref="PropRenderer"/> only when the layer draws through a live <see cref="Scene3D"/>
        /// AND that scene's resolved shadow tier is <see cref="ShadowMode.Blob"/>. The merged HLOD mesh never
        /// contributes a blob (no per-placement data there), so blobs stop at the HLOD boundary automatically.</summary>
        public IReadOnlyDictionary<string, float>? BlobRadii { get; }
        /// <summary>The single-handle mesh set: one <see cref="MeshHandle"/> per kit id (the flat/untextured form).
        /// Always non-null: an empty dictionary for a multi-part layer (which carries its meshes in
        /// <see cref="PartMeshes"/> instead).</summary>
        public IReadOnlyDictionary<string, MeshHandle> Meshes { get; }
        /// <summary>The multi-part mesh set (additive): each kit id maps to one-or-many <see cref="MeshHandle"/>s (one
        /// textured sub-mesh per source material, from <see cref="Scene3D.LoadPropMeshes"/>), instanced as a unit at
        /// each placement. Non-null only for a layer built by the multi-part factory overloads, null for a
        /// single-handle layer, which draws through <see cref="Meshes"/>. The sink draws whichever is set.</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>>? PartMeshes { get; }
        public float DrawRadius { get; }
        /// <summary>Width of the dissolve FADE BAND just inside <see cref="DrawRadius"/> (issue #44): over the ring
        /// <c>DrawRadius - FadeBandWidth</c> .. <c>DrawRadius</c> a prop's rigid dissolve ramps deterministically 0
        /// (solid) to 1 (fully discarded) by horizontal distance, so props thin out with a noise mask instead of
        /// popping at the hard cut. Default 0 = today's hard cut (no dissolve, byte-identical to before). A value
        /// wider than <see cref="DrawRadius"/> is clamped so the fade starts at the focus. See
        /// <see cref="PropRenderer"/>.</summary>
        public float FadeBandWidth { get; }
        /// <summary>Optional far LOD variants for a single-handle layer: one <see cref="MeshHandle"/> per kit id (an
        /// author-supplied low-poly mesh from <see cref="AssetEntry.LodFile"/> via
        /// <c>PropLoader.LoadPropLodAuto</c>). Null when the layer has no variants. Only consulted when
        /// <see cref="LodDistance"/> &gt; 0: a placement beyond that horizontal distance whose id is present here
        /// draws its LOD mesh instead of the full one, and any id NOT present keeps its full mesh (per-kit opt-in).</summary>
        public IReadOnlyDictionary<string, MeshHandle>? LodMeshes { get; }
        /// <summary>Optional far LOD variants for a MULTI-PART layer: each kit id maps to its LOD variant's part
        /// handles, parallel to <see cref="PartMeshes"/>. Null when the layer has no variants. Same
        /// <see cref="LodDistance"/> switch and same per-kit opt-in as <see cref="LodMeshes"/>.</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>>? LodPartMeshes { get; }
        /// <summary>Horizontal distance at which this layer switches a kit to its far LOD variant (see
        /// <see cref="LodMeshes"/> / <see cref="LodPartMeshes"/>). Default 0 = never switch (every prop draws its full
        /// mesh, unchanged behaviour). Only meaningful when the layer carries LOD variants.</summary>
        public float LodDistance { get; }

        /// <summary>Per-kit FLAT source meshes for the layer's HLOD merge (the <c>PropLoader.LoadProp</c> vertex-colour
        /// form, or an authored low-poly proxy). Null when the layer has no HLOD (the default). When set with a positive
        /// <see cref="HlodDistance"/>, <see cref="Scene3DChunkSink"/> bakes ONE coarse merged mesh per chunk cluster
        /// from this layer's placements (<see cref="PropHlod.BuildMergedMesh"/>, welded at <see cref="HlodWeldCell"/>)
        /// and renders it as a single instance beyond <see cref="HlodDistance"/> in place of the individual props. A
        /// per-kit opt-in like the LOD set: a placement whose id is absent contributes nothing to the merge.</summary>
        public IReadOnlyDictionary<string, GltfMesh>? HlodSourceMeshes { get; }
        /// <summary>Horizontal distance (chunk-centre to focus) at which this layer's chunk cluster swaps its
        /// individual props for the merged HLOD mesh. Default 0 = no HLOD (every chunk always draws its props,
        /// byte-identical to before). Only meaningful with <see cref="HlodSourceMeshes"/> set.</summary>
        public float HlodDistance { get; }
        /// <summary>Vertex-cluster weld cell size (metres) for the HLOD merge: source vertices in the same cubic cell
        /// collapse to one, cutting the triangle count (see <see cref="PropHlod.Weld"/>). A non-positive cell keeps the
        /// full-detail merge (no decimation). Only meaningful with <see cref="HlodSourceMeshes"/> set.</summary>
        public float HlodWeldCell { get; }
        /// <summary>Width (metres) of the crossfade band centred on <see cref="HlodDistance"/>: across it the props
        /// dissolve out and the merged HLOD mesh dissolves in (both via the 14.5.0 rigid-dissolve primitive,
        /// deterministic by distance). Default 0 = a hard swap at <see cref="HlodDistance"/>. See
        /// <see cref="PropHlod.CrossfadeAt"/>.</summary>
        public float HlodCrossfadeWidth { get; }

        static readonly IReadOnlyDictionary<string, MeshHandle> EmptyMeshes = new Dictionary<string, MeshHandle>();

        // Invariant (enforced by the private ctor + ScatterLayer/CompanionLayer/PlacementLayer factories): exactly
        // one of Scatter, Companions, or (Placements | PlacementSource) is set - a scatter layer has a non-null
        // Scatter, a companion layer a non-null Companions, and a placement layer (issue #286) either a frozen
        // Placements list or a live PlacementSource, never both, with the other two null. Exactly one of Meshes
        // (single-handle) / PartMeshes (multi-part) carries the layer's props. The other is empty/null. LOD
        // variants (when present) match that representation: LodMeshes for a single-handle layer, LodPartMeshes
        // for a multi-part one.
        public bool IsCompanion => Companions != null;
        /// <summary>True for a placement layer (issue #286): its props come from exact author-supplied placements
        /// instead of runtime procedural generation, either a frozen <see cref="Placements"/> list or a live
        /// <see cref="PlacementSource"/>. False for a scatter or companion layer.</summary>
        public bool IsPlacement => Placements != null || PlacementSource != null;

        /// <summary>True when this layer bakes and draws an HLOD merged mesh: it has HLOD source meshes AND a positive
        /// <see cref="HlodDistance"/>. When false the layer always draws its individual props (unchanged behaviour).</summary>
        public bool HasHlod => HlodSourceMeshes != null && HlodDistance > 0f;

        PropLayer(ScatterConfig? scatter, CompanionConfig? companions, int hostLayerIndex,
                  IReadOnlyDictionary<string, MeshHandle> meshes,
                  IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>>? partMeshes, float drawRadius,
                  float fadeBandWidth,
                  IReadOnlyDictionary<string, MeshHandle>? lodMeshes,
                  IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>>? lodPartMeshes, float lodDistance,
                  IReadOnlyDictionary<string, GltfMesh>? hlodSourceMeshes = null,
                  float hlodDistance = 0f, float hlodWeldCell = 0f, float hlodCrossfadeWidth = 0f,
                  IReadOnlyList<PropPlacement>? placements = null, bool registerColliders = true,
                  IPlacementSource? placementSource = null, bool castsShadows = true,
                  IReadOnlyDictionary<string, float>? blobRadii = null)
        {
            Scatter = scatter;
            Companions = companions;
            HostLayerIndex = hostLayerIndex;
            Meshes = meshes;
            PartMeshes = partMeshes;
            DrawRadius = drawRadius;
            FadeBandWidth = fadeBandWidth;
            LodMeshes = lodMeshes;
            LodPartMeshes = lodPartMeshes;
            LodDistance = lodDistance;
            HlodSourceMeshes = hlodSourceMeshes;
            HlodDistance = hlodDistance;
            HlodWeldCell = hlodWeldCell;
            HlodCrossfadeWidth = hlodCrossfadeWidth;
            Placements = placements;
            RegisterColliders = registerColliders;
            PlacementSource = placementSource;
            CastsShadows = castsShadows;
            BlobRadii = blobRadii;
        }

        /// <summary>This layer with HLOD turned on: a copy carrying the per-kit flat <paramref name="sourceMeshes"/> to
        /// merge, the <paramref name="hlodDistance"/> at which a chunk cluster swaps to the merged mesh, the
        /// <paramref name="weldCell"/> the merge decimates at, and an optional <paramref name="crossfadeWidth"/>.
        /// Applies to either representation (single-handle or multi-part) - the individual props still draw through
        /// their own mesh set, and the HLOD merged mesh (built from the flat source meshes) takes over past the
        /// distance. Keeps every other knob (fade band, LOD variants, draw radius) intact.</summary>
        public PropLayer WithHlod(IReadOnlyDictionary<string, GltfMesh> sourceMeshes, float hlodDistance,
                                  float weldCell, float crossfadeWidth = 0f)
        {
            if (sourceMeshes == null) throw new ArgumentNullException(nameof(sourceMeshes));
            return new PropLayer(Scatter, Companions, HostLayerIndex, Meshes, PartMeshes, DrawRadius, FadeBandWidth,
                LodMeshes, LodPartMeshes, LodDistance, sourceMeshes, hlodDistance, weldCell, crossfadeWidth,
                Placements, RegisterColliders, PlacementSource, CastsShadows, BlobRadii);
        }

        /// <summary>A scatter layer driven by its own <see cref="ScatterConfig"/> (single-handle mesh set).
        /// <paramref name="fadeBandWidth"/> (default 0 = hard cut) is the dissolve fade band just inside
        /// <paramref name="drawRadius"/> (see <see cref="FadeBandWidth"/>). Optional <paramref name="lodMeshes"/> plus
        /// a positive <paramref name="lodDistance"/> swap a kit to its far LOD variant beyond that distance (see
        /// <see cref="LodMeshes"/>).
        /// <paramref name="castsShadows"/> defaults true. Pass false to keep the whole layer out of the shadow
        /// depth pass (see <see cref="CastsShadows"/>). <paramref name="blobRadii"/> (default null) opts kits into a
        /// <see cref="ShadowMode.Blob"/> ground blob (see <see cref="BlobRadii"/>).</summary>
        public static PropLayer ScatterLayer(ScatterConfig scatter,
            IReadOnlyDictionary<string, MeshHandle> meshes, float drawRadius, float fadeBandWidth = 0f,
            IReadOnlyDictionary<string, MeshHandle>? lodMeshes = null, float lodDistance = 0f, bool castsShadows = true,
            IReadOnlyDictionary<string, float>? blobRadii = null)
        {
            if (scatter == null) throw new ArgumentNullException(nameof(scatter));
            if (meshes == null) throw new ArgumentNullException(nameof(meshes));
            return new PropLayer(scatter, null, -1, meshes, null, drawRadius, fadeBandWidth, lodMeshes, null, lodDistance,
                castsShadows: castsShadows, blobRadii: blobRadii);
        }

        /// <summary>A scatter layer whose kits are MULTI-PART (each id one-or-many <see cref="MeshHandle"/>s, one
        /// textured sub-mesh per material). Additive companion to the single-handle overload. Every part instances at
        /// each placement transform. Feed it <see cref="Scene3D.LoadPropMeshes"/> output per id.
        /// <paramref name="fadeBandWidth"/> and (<paramref name="lodPartMeshes"/>, <paramref name="lodDistance"/>) work
        /// as on the single-handle overload, with LOD variants supplied as parallel part lists.
        /// <paramref name="castsShadows"/> defaults true. Pass false to keep the whole layer out of the shadow
        /// depth pass (see <see cref="CastsShadows"/>). <paramref name="blobRadii"/> (default null) opts kits into a
        /// <see cref="ShadowMode.Blob"/> ground blob (see <see cref="BlobRadii"/>).</summary>
        public static PropLayer ScatterLayer(ScatterConfig scatter,
            IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> partMeshes, float drawRadius, float fadeBandWidth = 0f,
            IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>>? lodPartMeshes = null, float lodDistance = 0f,
            bool castsShadows = true, IReadOnlyDictionary<string, float>? blobRadii = null)
        {
            if (scatter == null) throw new ArgumentNullException(nameof(scatter));
            if (partMeshes == null) throw new ArgumentNullException(nameof(partMeshes));
            return new PropLayer(scatter, null, -1, EmptyMeshes, partMeshes, drawRadius, fadeBandWidth, null, lodPartMeshes,
                lodDistance, castsShadows: castsShadows, blobRadii: blobRadii);
        }

        /// <summary>A companion layer: rings the placements of the scatter layer at <paramref name="hostLayerIndex"/>
        /// with foliage per <paramref name="companions"/> (single-handle mesh set). <paramref name="fadeBandWidth"/>
        /// and (<paramref name="lodMeshes"/>, <paramref name="lodDistance"/>) behave as on the scatter overload.
        /// <paramref name="castsShadows"/> defaults true. Pass false to keep the whole layer out of the shadow
        /// depth pass (see <see cref="CastsShadows"/>). <paramref name="blobRadii"/> (default null) opts kits into a
        /// <see cref="ShadowMode.Blob"/> ground blob (see <see cref="BlobRadii"/>).</summary>
        public static PropLayer CompanionLayer(int hostLayerIndex, CompanionConfig companions,
            IReadOnlyDictionary<string, MeshHandle> meshes, float drawRadius, float fadeBandWidth = 0f,
            IReadOnlyDictionary<string, MeshHandle>? lodMeshes = null, float lodDistance = 0f, bool castsShadows = true,
            IReadOnlyDictionary<string, float>? blobRadii = null)
        {
            if (companions == null) throw new ArgumentNullException(nameof(companions));
            if (meshes == null) throw new ArgumentNullException(nameof(meshes));
            if (hostLayerIndex < 0) throw new ArgumentOutOfRangeException(nameof(hostLayerIndex));
            return new PropLayer(null, companions, hostLayerIndex, meshes, null, drawRadius, fadeBandWidth, lodMeshes, null,
                lodDistance, castsShadows: castsShadows, blobRadii: blobRadii);
        }

        /// <summary>A companion layer whose kits are MULTI-PART (additive companion to the single-handle overload).
        /// Each id's parts instance as a unit around every host placement. <paramref name="fadeBandWidth"/> and
        /// (<paramref name="lodPartMeshes"/>, <paramref name="lodDistance"/>) behave as on the scatter overload.
        /// <paramref name="castsShadows"/> defaults true. Pass false to keep the whole layer out of the shadow
        /// depth pass (see <see cref="CastsShadows"/>). <paramref name="blobRadii"/> (default null) opts kits into a
        /// <see cref="ShadowMode.Blob"/> ground blob (see <see cref="BlobRadii"/>).</summary>
        public static PropLayer CompanionLayer(int hostLayerIndex, CompanionConfig companions,
            IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> partMeshes, float drawRadius, float fadeBandWidth = 0f,
            IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>>? lodPartMeshes = null, float lodDistance = 0f,
            bool castsShadows = true, IReadOnlyDictionary<string, float>? blobRadii = null)
        {
            if (companions == null) throw new ArgumentNullException(nameof(companions));
            if (partMeshes == null) throw new ArgumentNullException(nameof(partMeshes));
            if (hostLayerIndex < 0) throw new ArgumentOutOfRangeException(nameof(hostLayerIndex));
            return new PropLayer(null, companions, hostLayerIndex, EmptyMeshes, partMeshes, drawRadius, fadeBandWidth, null,
                lodPartMeshes, lodDistance, castsShadows: castsShadows, blobRadii: blobRadii);
        }

        /// <summary>A placement layer: a frozen, author-supplied list of exact <paramref name="placements"/>
        /// (issue #286), streamed by the sink exactly like a scatter or companion layer, with no procedural
        /// generation - e.g. a frozen zone's authored decor (single-handle mesh set). The sink buckets
        /// <paramref name="placements"/> by chunk coordinate once at construction, and every knob the scatter
        /// overload supports - <paramref name="fadeBandWidth"/>, (<paramref name="lodMeshes"/>,
        /// <paramref name="lodDistance"/>), and <see cref="WithHlod"/> - applies unchanged. <paramref name="colliders"/>
        /// defaults true. Pass false to keep the layer render-only when the consuming game registers physics for
        /// these placements outside the sink.
        /// <paramref name="castsShadows"/> defaults true. Pass false to keep the whole layer out of the shadow
        /// depth pass (see <see cref="CastsShadows"/>). <paramref name="blobRadii"/> (default null) opts kits into a
        /// <see cref="ShadowMode.Blob"/> ground blob (see <see cref="BlobRadii"/>).</summary>
        public static PropLayer PlacementLayer(IReadOnlyList<PropPlacement> placements,
            IReadOnlyDictionary<string, MeshHandle> meshes, float drawRadius, float fadeBandWidth = 0f,
            IReadOnlyDictionary<string, MeshHandle>? lodMeshes = null, float lodDistance = 0f, bool colliders = true,
            bool castsShadows = true, IReadOnlyDictionary<string, float>? blobRadii = null)
        {
            if (placements == null) throw new ArgumentNullException(nameof(placements));
            if (meshes == null) throw new ArgumentNullException(nameof(meshes));
            return new PropLayer(null, null, -1, meshes, null, drawRadius, fadeBandWidth, lodMeshes, null, lodDistance,
                placements: placements, registerColliders: colliders, castsShadows: castsShadows, blobRadii: blobRadii);
        }

        /// <summary>A placement layer whose kits are MULTI-PART (additive companion to the single-handle overload):
        /// each id's parts instance as a unit at every placement. Same frozen, author-supplied
        /// <paramref name="placements"/> (issue #286), the same bucket-once-at-construction behaviour, and the same
        /// knob set as the single-handle overload, with LOD variants supplied as parallel part lists via
        /// <paramref name="lodPartMeshes"/>. <paramref name="colliders"/> defaults true. Pass false to keep the
        /// layer render-only when the consuming game registers physics for these placements outside the sink.
        /// <paramref name="castsShadows"/> defaults true. Pass false to keep the whole layer out of the shadow
        /// depth pass (see <see cref="CastsShadows"/>). <paramref name="blobRadii"/> (default null) opts kits into a
        /// <see cref="ShadowMode.Blob"/> ground blob (see <see cref="BlobRadii"/>).</summary>
        public static PropLayer PlacementLayer(IReadOnlyList<PropPlacement> placements,
            IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> partMeshes, float drawRadius, float fadeBandWidth = 0f,
            IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>>? lodPartMeshes = null, float lodDistance = 0f,
            bool colliders = true, bool castsShadows = true, IReadOnlyDictionary<string, float>? blobRadii = null)
        {
            if (placements == null) throw new ArgumentNullException(nameof(placements));
            if (partMeshes == null) throw new ArgumentNullException(nameof(partMeshes));
            return new PropLayer(null, null, -1, EmptyMeshes, partMeshes, drawRadius, fadeBandWidth, null, lodPartMeshes,
                lodDistance, placements: placements, registerColliders: colliders, castsShadows: castsShadows,
                blobRadii: blobRadii);
        }

        /// <summary>A placement layer backed by a LIVE <paramref name="source"/> instead of a frozen list: the
        /// sink asks the source for the chunk's placements at every build, so content that arrives after the sink
        /// was constructed (a streamed document tile) renders as soon as its chunks are invalidated. A frozen-list
        /// layer is bucketed once at construction and cannot do that. Every knob the frozen overload supports -
        /// <paramref name="fadeBandWidth"/>, (<paramref name="lodMeshes"/>, <paramref name="lodDistance"/>), and
        /// <see cref="WithHlod"/> - applies unchanged, and so do the collider and casts-shadows flags.
        /// <para>The source is queried on the BUILD thread, so it must publish an immutable snapshot and read it
        /// once per query. <c>MapTileResidency</c> is one, which makes
        /// <c>PropLayer.PlacementLayer(residency, meshes, drawRadius)</c> the whole of a consumer's wiring.</para>
        /// <paramref name="blobRadii"/> (default null) opts kits into a <see cref="ShadowMode.Blob"/> ground blob
        /// (see <see cref="BlobRadii"/>).</summary>
        public static PropLayer PlacementLayer(IPlacementSource source,
            IReadOnlyDictionary<string, MeshHandle> meshes, float drawRadius, float fadeBandWidth = 0f,
            IReadOnlyDictionary<string, MeshHandle>? lodMeshes = null, float lodDistance = 0f, bool colliders = true,
            bool castsShadows = true, IReadOnlyDictionary<string, float>? blobRadii = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (meshes == null) throw new ArgumentNullException(nameof(meshes));
            return new PropLayer(null, null, -1, meshes, null, drawRadius, fadeBandWidth, lodMeshes, null, lodDistance,
                registerColliders: colliders, placementSource: source, castsShadows: castsShadows, blobRadii: blobRadii);
        }

        /// <summary>A live-source placement layer whose kits are MULTI-PART (additive companion to the
        /// single-handle overload): each id's parts instance as a unit at every placement the source serves for
        /// the chunk. Same per-build query, same knob set, with LOD variants supplied as parallel part lists via
        /// <paramref name="lodPartMeshes"/>.
        /// <paramref name="castsShadows"/> defaults true. Pass false to keep the whole layer out of the shadow
        /// depth pass (see <see cref="CastsShadows"/>). <paramref name="blobRadii"/> (default null) opts kits into a
        /// <see cref="ShadowMode.Blob"/> ground blob (see <see cref="BlobRadii"/>).</summary>
        public static PropLayer PlacementLayer(IPlacementSource source,
            IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> partMeshes, float drawRadius, float fadeBandWidth = 0f,
            IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>>? lodPartMeshes = null, float lodDistance = 0f,
            bool colliders = true, bool castsShadows = true, IReadOnlyDictionary<string, float>? blobRadii = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (partMeshes == null) throw new ArgumentNullException(nameof(partMeshes));
            return new PropLayer(null, null, -1, EmptyMeshes, partMeshes, drawRadius, fadeBandWidth, null, lodPartMeshes,
                lodDistance, registerColliders: colliders, placementSource: source, castsShadows: castsShadows,
                blobRadii: blobRadii);
        }
    }
}
