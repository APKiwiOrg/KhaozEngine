using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain
{
    /// <summary>One generation-tagged prop cluster built without GPU access.</summary>
    public sealed class PropClusterCpuBuild
    {
        public PropClusterKey Key { get; }
        public long Generation { get; }
        public RectArea Area { get; }
        public PropLayer Layer { get; }
        public IReadOnlyList<PropPlacement> PlacementBatch { get; }
        public GltfMesh? MergedMesh { get; }
        public bool Succeeded => Failure is null;

        internal bool ReusesCurrent { get; }
        internal bool CountsHlod { get; }
        internal Exception? Failure { get; }
        internal long Epoch { get; }

        internal PropClusterCpuBuild(PropClusterKey key, long generation, RectArea area, PropLayer layer,
                                     IReadOnlyList<PropPlacement> placements, GltfMesh? mergedMesh,
                                     bool reusesCurrent, bool countsHlod, Exception? failure, long epoch)
        {
            Key = key;
            Generation = generation;
            Area = area;
            Layer = layer;
            PlacementBatch = placements;
            MergedMesh = mergedMesh;
            ReusesCurrent = reusesCurrent;
            CountsHlod = countsHlod;
            Failure = failure;
            Epoch = epoch;
        }

        internal PropClusterCpuBuild WithPlacementBatch(IReadOnlyList<PropPlacement> placements) =>
            new(Key, Generation, Area, Layer, placements, MergedMesh, ReusesCurrent, CountsHlod, Failure, Epoch);
    }
}
