using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain
{
    /// <summary>One prop layer for the multi-layer <see cref="Scene3DChunkSink"/>: either a <em>scatter</em> layer
    /// (an independent <see cref="ScatterConfig"/>, e.g. sparse trees at a long draw radius) or a <em>companion</em>
    /// layer (a <see cref="CompanionConfig"/> + the index of its host scatter layer, e.g. understory foliage rung
    /// around the trees at a short draw radius). Each layer carries its own mesh set and draw radius - the short
    /// radius on a dense layer is what keeps it affordable, and each layer sets its own dissolve fade band
    /// (<see cref="FadeBandWidth"/>) and optional far LOD variants (<see cref="LodMeshes"/> / <see cref="LodPartMeshes"/>
    /// at <see cref="LodDistance"/>). Build one with
    /// <see cref="ScatterLayer(ScatterConfig, IReadOnlyDictionary{string, MeshHandle}, float, float, IReadOnlyDictionary{string, MeshHandle}, float)"/> or
    /// <see cref="CompanionLayer(int, CompanionConfig, IReadOnlyDictionary{string, MeshHandle}, float, float, IReadOnlyDictionary{string, MeshHandle}, float)"/> (or their
    /// multi-part overloads).</summary>
    public readonly struct PropLayer
    {
        public ScatterConfig? Scatter { get; }
        public CompanionConfig? Companions { get; }
        /// <summary>For a companion layer, the index (into the sink's layer list) of the scatter layer whose
        /// placements are the hosts. Unused (-1) for a scatter layer.</summary>
        public int HostLayerIndex { get; }
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

        // Invariant (enforced by the private ctor + ScatterLayer/CompanionLayer factories): a non-companion
        // layer always has a non-null Scatter, and a companion layer always has a non-null Companions. Exactly one of
        // Meshes (single-handle) / PartMeshes (multi-part) carries the layer's props. The other is empty/null. LOD
        // variants (when present) match that representation: LodMeshes for a single-handle layer, LodPartMeshes for a
        // multi-part one.
        public bool IsCompanion => Companions != null;

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
                  float hlodDistance = 0f, float hlodWeldCell = 0f, float hlodCrossfadeWidth = 0f)
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
                LodMeshes, LodPartMeshes, LodDistance, sourceMeshes, hlodDistance, weldCell, crossfadeWidth);
        }

        /// <summary>A scatter layer driven by its own <see cref="ScatterConfig"/> (single-handle mesh set).
        /// <paramref name="fadeBandWidth"/> (default 0 = hard cut) is the dissolve fade band just inside
        /// <paramref name="drawRadius"/> (see <see cref="FadeBandWidth"/>). Optional <paramref name="lodMeshes"/> plus
        /// a positive <paramref name="lodDistance"/> swap a kit to its far LOD variant beyond that distance (see
        /// <see cref="LodMeshes"/>).</summary>
        public static PropLayer ScatterLayer(ScatterConfig scatter,
            IReadOnlyDictionary<string, MeshHandle> meshes, float drawRadius, float fadeBandWidth = 0f,
            IReadOnlyDictionary<string, MeshHandle>? lodMeshes = null, float lodDistance = 0f)
        {
            if (scatter == null) throw new ArgumentNullException(nameof(scatter));
            if (meshes == null) throw new ArgumentNullException(nameof(meshes));
            return new PropLayer(scatter, null, -1, meshes, null, drawRadius, fadeBandWidth, lodMeshes, null, lodDistance);
        }

        /// <summary>A scatter layer whose kits are MULTI-PART (each id one-or-many <see cref="MeshHandle"/>s, one
        /// textured sub-mesh per material). Additive companion to the single-handle overload. Every part instances at
        /// each placement transform. Feed it <see cref="Scene3D.LoadPropMeshes"/> output per id.
        /// <paramref name="fadeBandWidth"/> and (<paramref name="lodPartMeshes"/>, <paramref name="lodDistance"/>) work
        /// as on the single-handle overload, with LOD variants supplied as parallel part lists.</summary>
        public static PropLayer ScatterLayer(ScatterConfig scatter,
            IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> partMeshes, float drawRadius, float fadeBandWidth = 0f,
            IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>>? lodPartMeshes = null, float lodDistance = 0f)
        {
            if (scatter == null) throw new ArgumentNullException(nameof(scatter));
            if (partMeshes == null) throw new ArgumentNullException(nameof(partMeshes));
            return new PropLayer(scatter, null, -1, EmptyMeshes, partMeshes, drawRadius, fadeBandWidth, null, lodPartMeshes, lodDistance);
        }

        /// <summary>A companion layer: rings the placements of the scatter layer at <paramref name="hostLayerIndex"/>
        /// with foliage per <paramref name="companions"/> (single-handle mesh set). <paramref name="fadeBandWidth"/>
        /// and (<paramref name="lodMeshes"/>, <paramref name="lodDistance"/>) behave as on the scatter overload.</summary>
        public static PropLayer CompanionLayer(int hostLayerIndex, CompanionConfig companions,
            IReadOnlyDictionary<string, MeshHandle> meshes, float drawRadius, float fadeBandWidth = 0f,
            IReadOnlyDictionary<string, MeshHandle>? lodMeshes = null, float lodDistance = 0f)
        {
            if (companions == null) throw new ArgumentNullException(nameof(companions));
            if (meshes == null) throw new ArgumentNullException(nameof(meshes));
            if (hostLayerIndex < 0) throw new ArgumentOutOfRangeException(nameof(hostLayerIndex));
            return new PropLayer(null, companions, hostLayerIndex, meshes, null, drawRadius, fadeBandWidth, lodMeshes, null, lodDistance);
        }

        /// <summary>A companion layer whose kits are MULTI-PART (additive companion to the single-handle overload).
        /// Each id's parts instance as a unit around every host placement. <paramref name="fadeBandWidth"/> and
        /// (<paramref name="lodPartMeshes"/>, <paramref name="lodDistance"/>) behave as on the scatter overload.</summary>
        public static PropLayer CompanionLayer(int hostLayerIndex, CompanionConfig companions,
            IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> partMeshes, float drawRadius, float fadeBandWidth = 0f,
            IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>>? lodPartMeshes = null, float lodDistance = 0f)
        {
            if (companions == null) throw new ArgumentNullException(nameof(companions));
            if (partMeshes == null) throw new ArgumentNullException(nameof(partMeshes));
            if (hostLayerIndex < 0) throw new ArgumentOutOfRangeException(nameof(hostLayerIndex));
            return new PropLayer(null, companions, hostLayerIndex, EmptyMeshes, partMeshes, drawRadius, fadeBandWidth, null, lodPartMeshes, lodDistance);
        }
    }
}
