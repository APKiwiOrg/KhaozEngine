using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain
{
    /// <summary>One prop layer for the multi-layer <see cref="Scene3DChunkSink"/>: either a <em>scatter</em> layer
    /// (an independent <see cref="ScatterConfig"/>, e.g. sparse trees at a long draw radius) or a <em>companion</em>
    /// layer (a <see cref="CompanionConfig"/> + the index of its host scatter layer, e.g. understory foliage rung
    /// around the trees at a short draw radius). Each layer carries its own mesh set and draw radius - the short
    /// radius on a dense layer is what keeps it affordable. Build one with
    /// <see cref="ScatterLayer(ScatterConfig, IReadOnlyDictionary{string, MeshHandle}, float)"/> or
    /// <see cref="CompanionLayer(int, CompanionConfig, IReadOnlyDictionary{string, MeshHandle}, float)"/> (or their
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

        static readonly IReadOnlyDictionary<string, MeshHandle> EmptyMeshes = new Dictionary<string, MeshHandle>();

        // Invariant (enforced by the private ctor + ScatterLayer/CompanionLayer factories): a non-companion
        // layer always has a non-null Scatter, and a companion layer always has a non-null Companions. Exactly one of
        // Meshes (single-handle) / PartMeshes (multi-part) carries the layer's props. The other is empty/null.
        public bool IsCompanion => Companions != null;

        PropLayer(ScatterConfig? scatter, CompanionConfig? companions, int hostLayerIndex,
                  IReadOnlyDictionary<string, MeshHandle> meshes,
                  IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>>? partMeshes, float drawRadius)
        {
            Scatter = scatter;
            Companions = companions;
            HostLayerIndex = hostLayerIndex;
            Meshes = meshes;
            PartMeshes = partMeshes;
            DrawRadius = drawRadius;
        }

        /// <summary>A scatter layer driven by its own <see cref="ScatterConfig"/> (single-handle mesh set).</summary>
        public static PropLayer ScatterLayer(ScatterConfig scatter,
            IReadOnlyDictionary<string, MeshHandle> meshes, float drawRadius)
        {
            if (scatter == null) throw new ArgumentNullException(nameof(scatter));
            if (meshes == null) throw new ArgumentNullException(nameof(meshes));
            return new PropLayer(scatter, null, -1, meshes, null, drawRadius);
        }

        /// <summary>A scatter layer whose kits are MULTI-PART (each id one-or-many <see cref="MeshHandle"/>s, one
        /// textured sub-mesh per material). Additive companion to the single-handle overload. Every part instances at
        /// each placement transform. Feed it <see cref="Scene3D.LoadPropMeshes"/> output per id.</summary>
        public static PropLayer ScatterLayer(ScatterConfig scatter,
            IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> partMeshes, float drawRadius)
        {
            if (scatter == null) throw new ArgumentNullException(nameof(scatter));
            if (partMeshes == null) throw new ArgumentNullException(nameof(partMeshes));
            return new PropLayer(scatter, null, -1, EmptyMeshes, partMeshes, drawRadius);
        }

        /// <summary>A companion layer: rings the placements of the scatter layer at <paramref name="hostLayerIndex"/>
        /// with foliage per <paramref name="companions"/> (single-handle mesh set).</summary>
        public static PropLayer CompanionLayer(int hostLayerIndex, CompanionConfig companions,
            IReadOnlyDictionary<string, MeshHandle> meshes, float drawRadius)
        {
            if (companions == null) throw new ArgumentNullException(nameof(companions));
            if (meshes == null) throw new ArgumentNullException(nameof(meshes));
            if (hostLayerIndex < 0) throw new ArgumentOutOfRangeException(nameof(hostLayerIndex));
            return new PropLayer(null, companions, hostLayerIndex, meshes, null, drawRadius);
        }

        /// <summary>A companion layer whose kits are MULTI-PART (additive companion to the single-handle overload).
        /// Each id's parts instance as a unit around every host placement.</summary>
        public static PropLayer CompanionLayer(int hostLayerIndex, CompanionConfig companions,
            IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> partMeshes, float drawRadius)
        {
            if (companions == null) throw new ArgumentNullException(nameof(companions));
            if (partMeshes == null) throw new ArgumentNullException(nameof(partMeshes));
            if (hostLayerIndex < 0) throw new ArgumentOutOfRangeException(nameof(hostLayerIndex));
            return new PropLayer(null, companions, hostLayerIndex, EmptyMeshes, partMeshes, drawRadius);
        }
    }
}
