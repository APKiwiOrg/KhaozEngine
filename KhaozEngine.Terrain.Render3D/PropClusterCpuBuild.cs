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
        /// <summary>Original render placements retained for draw filtering even when the active individual batch
        /// is empty, as it is for a render-only decor chunk.</summary>
        internal IReadOnlyList<PropPlacement> FilterPlacementBatch { get; }
        public GltfMesh? MergedMesh { get; }
        public bool Succeeded => Failure is null;

        internal bool ReusesCurrent { get; }
        internal bool PreservesFilterPlacementBatch { get; }
        internal bool CountsHlod { get; }
        internal Exception? Failure { get; }
        internal long Epoch { get; }

        internal PropClusterCpuBuild(PropClusterKey key, long generation, RectArea area, PropLayer layer,
                                     IReadOnlyList<PropPlacement> placements, GltfMesh? mergedMesh,
                                     bool reusesCurrent, bool countsHlod, Exception? failure, long epoch)
            : this(key, generation, area, layer, placements, placements, mergedMesh, reusesCurrent,
                countsHlod, failure, epoch, preservesFilterPlacementBatch: false)
        {
        }

        PropClusterCpuBuild(PropClusterKey key, long generation, RectArea area, PropLayer layer,
                            IReadOnlyList<PropPlacement> placements,
                            IReadOnlyList<PropPlacement> filterPlacements, GltfMesh? mergedMesh,
                            bool reusesCurrent, bool countsHlod, Exception? failure, long epoch,
                            bool preservesFilterPlacementBatch)
        {
            Key = key;
            Generation = generation;
            Area = area;
            Layer = layer;
            PlacementBatch = placements;
            FilterPlacementBatch = filterPlacements;
            MergedMesh = mergedMesh;
            ReusesCurrent = reusesCurrent;
            PreservesFilterPlacementBatch = preservesFilterPlacementBatch;
            CountsHlod = countsHlod;
            Failure = failure;
            Epoch = epoch;
        }

        internal PropClusterCpuBuild WithPlacementBatch(IReadOnlyList<PropPlacement> placements,
            bool preserveFilterPlacementBatch = false) =>
            new(Key, Generation, Area, Layer, placements, FilterPlacementBatch, MergedMesh, ReusesCurrent,
                CountsHlod, Failure, Epoch, preserveFilterPlacementBatch);
    }
}
