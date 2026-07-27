using System.Collections.Generic;

namespace KhaozEngine.Terrain
{
    /// <summary>Buckets a placement layer's explicit world-space placements by chunk coord, once at sink
    /// construction (placements are static, so the split is computed exactly once and served per chunk).
    /// Uses <see cref="ChunkGrid.CoordOf"/>, the same floor-toward-negative grid math the streamer loads
    /// with, so every placement lands in exactly one chunk and tiling reproduces the whole list. Bucket
    /// order preserves input order (deterministic per input list). Internal and pure for headless tests.</summary>
    internal static class PlacementBuckets
    {
        /// <summary>One bucket map per layer, index-aligned to <paramref name="layers"/>: a FROZEN-LIST placement
        /// layer gets its placements split by chunk coord, every other layer gets null. A SOURCE-BACKED placement
        /// layer is skipped deliberately - its placements are queried live at every build, so bucketing them once
        /// here is exactly the staleness it exists to avoid. Returns null when no layer carries a frozen list, so
        /// a sink with only scatter and source-backed layers stores nothing new.</summary>
        internal static Dictionary<ChunkCoord, PropPlacement[]>[]? Build(IReadOnlyList<PropLayer> layers, float chunkSize)
        {
            Dictionary<ChunkCoord, PropPlacement[]>[]? buckets = null;
            for (int i = 0; i < layers.Count; i++)
            {
                IReadOnlyList<PropPlacement>? placements = layers[i].Placements;
                if (placements == null) continue;
                buckets ??= new Dictionary<ChunkCoord, PropPlacement[]>[layers.Count];
                buckets[i] = BuildOne(placements, chunkSize);
            }
            return buckets;
        }

        static Dictionary<ChunkCoord, PropPlacement[]> BuildOne(IReadOnlyList<PropPlacement> placements, float chunkSize)
        {
            var lists = new Dictionary<ChunkCoord, List<PropPlacement>>();
            for (int i = 0; i < placements.Count; i++)
            {
                PropPlacement p = placements[i];
                ChunkCoord coord = ChunkGrid.CoordOf(p.X, p.Z, chunkSize);
                if (!lists.TryGetValue(coord, out List<PropPlacement>? list))
                    lists[coord] = list = new List<PropPlacement>();
                list.Add(p);
            }
            var buckets = new Dictionary<ChunkCoord, PropPlacement[]>(lists.Count);
            foreach (KeyValuePair<ChunkCoord, List<PropPlacement>> kv in lists)
                buckets[kv.Key] = kv.Value.ToArray();
            return buckets;
        }
    }
}
