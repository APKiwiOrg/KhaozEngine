using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain
{
    /// <summary>One prop layer for the multi-layer <see cref="Scene3DChunkSink"/>: either a <em>scatter</em> layer
    /// (an independent <see cref="ScatterConfig"/>, e.g. sparse trees at a long draw radius) or a <em>companion</em>
    /// layer (a <see cref="CompanionConfig"/> + the index of its host scatter layer, e.g. understory foliage rung
    /// around the trees at a short draw radius). Each layer carries its own mesh set and draw radius - the short
    /// radius on a dense layer is what keeps it affordable. Build one with <see cref="ScatterLayer"/> or
    /// <see cref="CompanionLayer"/>.</summary>
    public readonly struct PropLayer
    {
        public ScatterConfig? Scatter { get; }
        public CompanionConfig? Companions { get; }
        /// <summary>For a companion layer, the index (into the sink's layer list) of the scatter layer whose
        /// placements are the hosts. Unused (-1) for a scatter layer.</summary>
        public int HostLayerIndex { get; }
        public IReadOnlyDictionary<string, MeshHandle> Meshes { get; }
        public float DrawRadius { get; }

        public bool IsCompanion => Companions != null;

        PropLayer(ScatterConfig? scatter, CompanionConfig? companions, int hostLayerIndex,
                  IReadOnlyDictionary<string, MeshHandle> meshes, float drawRadius)
        {
            Scatter = scatter;
            Companions = companions;
            HostLayerIndex = hostLayerIndex;
            Meshes = meshes;
            DrawRadius = drawRadius;
        }

        /// <summary>A scatter layer driven by its own <see cref="ScatterConfig"/>.</summary>
        public static PropLayer ScatterLayer(ScatterConfig scatter,
            IReadOnlyDictionary<string, MeshHandle> meshes, float drawRadius)
        {
            if (scatter == null) throw new ArgumentNullException(nameof(scatter));
            if (meshes == null) throw new ArgumentNullException(nameof(meshes));
            return new PropLayer(scatter, null, -1, meshes, drawRadius);
        }

        /// <summary>A companion layer: rings the placements of the scatter layer at <paramref name="hostLayerIndex"/>
        /// with foliage per <paramref name="companions"/>.</summary>
        public static PropLayer CompanionLayer(int hostLayerIndex, CompanionConfig companions,
            IReadOnlyDictionary<string, MeshHandle> meshes, float drawRadius)
        {
            if (companions == null) throw new ArgumentNullException(nameof(companions));
            if (meshes == null) throw new ArgumentNullException(nameof(meshes));
            if (hostLayerIndex < 0) throw new ArgumentOutOfRangeException(nameof(hostLayerIndex));
            return new PropLayer(null, companions, hostLayerIndex, meshes, drawRadius);
        }
    }
}
