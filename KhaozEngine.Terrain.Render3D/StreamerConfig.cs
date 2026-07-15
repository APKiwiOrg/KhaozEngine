namespace KhaozEngine.Terrain
{
    /// <summary>Tuning for <see cref="TerrainStreamer"/>. <see cref="LoadRadius"/> / <see cref="UnloadRadius"/> are in
    /// CHUNK units (Euclidean chunk-distance); <see cref="UnloadRadius"/> must exceed <see cref="LoadRadius"/> so the
    /// hysteresis band stops churn when the player oscillates across a chunk boundary. LOD tiers come from
    /// <see cref="TerrainLod.PickLod"/> (metre distance to chunk center), not configured here.
    /// <para><see cref="Async"/> (default true): the CPU mesh build runs on a background thread and only the GPU
    /// upload happens on the frame thread. <see cref="MaxLoadsPerFrame"/> then caps how many completed builds are
    /// APPLIED (GPU upload + swap) per <c>Update</c>. The builds themselves are unbudgeted (they run in parallel off
    /// the frame thread). When <see cref="Async"/> is false, or the sink is not an <see cref="IAsyncChunkSink"/>, the
    /// streamer runs the old synchronous path where <see cref="MaxLoadsPerFrame"/> caps build+upload ops done inline.
    /// Either way unloads are immediate.</para></summary>
    public readonly record struct StreamerConfig(int LoadRadius, int UnloadRadius, int MaxLoadsPerFrame, float ChunkSize, bool Async = true)
    {
        /// <summary>LoadRadius 4 (~240 m view), UnloadRadius 6 (2-chunk hysteresis band), 3 applies/frame,
        /// 60 m chunks, async build on. A brisk run (6 m/s) crosses a chunk in ~10 s, far under the per-frame budget.</summary>
        public static StreamerConfig Default => new(LoadRadius: 4, UnloadRadius: 6, MaxLoadsPerFrame: 3, ChunkSize: TerrainChunkRegion.DefaultSize);

        /// <summary>This config with async build turned off (the old synchronous build+upload-on-the-frame-thread path).
        /// Handy for editors/tools that want blocking, deterministic loads: <c>StreamerConfig.Default.Synchronous()</c>.</summary>
        public StreamerConfig Synchronous() => this with { Async = false };
    }
}
