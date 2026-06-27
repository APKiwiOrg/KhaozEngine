namespace KhaozEngine.Terrain
{
    /// <summary>Tuning for <see cref="TerrainStreamer"/>. <see cref="LoadRadius"/> / <see cref="UnloadRadius"/> are in
    /// CHUNK units (Euclidean chunk-distance); <see cref="UnloadRadius"/> must exceed <see cref="LoadRadius"/> so the
    /// hysteresis band stops churn when the player oscillates across a chunk boundary. <see cref="MaxLoadsPerFrame"/>
    /// caps load + re-LOD ops per <c>Update</c> (unloads are immediate) so a build burst never hitches. LOD tiers come
    /// from <see cref="TerrainLod.PickLod"/> (metre distance to chunk center), not configured here.</summary>
    public readonly record struct StreamerConfig(int LoadRadius, int UnloadRadius, int MaxLoadsPerFrame, float ChunkSize)
    {
        /// <summary>LoadRadius 4 (~240 m view), UnloadRadius 6 (2-chunk hysteresis band), 3 builds/frame,
        /// 60 m chunks. A brisk run (6 m/s) crosses a chunk in ~10 s, far under the per-frame load budget.</summary>
        public static StreamerConfig Default => new(LoadRadius: 4, UnloadRadius: 6, MaxLoadsPerFrame: 3, ChunkSize: TerrainChunkRegion.DefaultSize);
    }
}
