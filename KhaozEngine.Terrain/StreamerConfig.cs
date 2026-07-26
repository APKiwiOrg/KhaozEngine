namespace KhaozEngine.Terrain
{
    /// <summary>Tuning for <see cref="TerrainStreamer"/>. <see cref="LoadRadius"/> / <see cref="DecorRadius"/> /
    /// <see cref="UnloadRadius"/> are in CHUNK units (Euclidean chunk-distance).
    /// <para><b>Rings.</b> <see cref="LoadRadius"/> is the GAMEPLAY radius: chunks within it are full
    /// <see cref="ChunkRing.Gameplay"/> chunks (scatter + prop colliders + optional terrain collision).
    /// <see cref="DecorRadius"/> is the far/decor radius: chunks between <see cref="LoadRadius"/> and
    /// <see cref="DecorRadius"/> are render-only <see cref="ChunkRing.Decor"/> chunks (coarse terrain mesh, no
    /// scatter or physics), so seeing the far field costs mesh only. The streamer loads out to the larger of the
    /// two; leaving <see cref="DecorRadius"/> at 0 (the default) means no decor ring, so every loaded chunk is
    /// gameplay - the pre-decor-ring behaviour exactly. <see cref="UnloadRadius"/> is the hysteresis unload boundary
    /// and MUST exceed the larger of <see cref="LoadRadius"/> / <see cref="DecorRadius"/> so oscillating across the
    /// outer edge does not churn.</para>
    /// <para><b>LOD.</b> LOD tiers come from <see cref="LodConfig"/> (null uses <see cref="TerrainLodConfig.Default"/>),
    /// picked by metre distance to chunk center. The SAME <see cref="LodConfig"/> must be wired to the sink so a tier
    /// index means the same grid resolution on both sides (both default, so the default wiring aligns for free).</para>
    /// <para><see cref="Async"/> (default true): the CPU mesh build runs on a background thread and only the GPU
    /// upload happens on the frame thread. <see cref="MaxLoadsPerFrame"/> then caps how many completed builds are
    /// APPLIED (GPU upload + swap) per <c>Update</c>. The builds themselves are unbudgeted (they run in parallel off
    /// the frame thread). When <see cref="Async"/> is false, or the sink is not an <see cref="IAsyncChunkSink"/>, the
    /// streamer runs the old synchronous path where <see cref="MaxLoadsPerFrame"/> caps build+upload ops done inline.
    /// Either way unloads are immediate.</para></summary>
    public readonly record struct StreamerConfig(int LoadRadius, int UnloadRadius, int MaxLoadsPerFrame, float ChunkSize, bool Async = true, int DecorRadius = 0, TerrainLodConfig? LodConfig = null)
    {
        /// <summary>LoadRadius 4 (~240 m gameplay disk), UnloadRadius 6 (2-chunk hysteresis band), 3 applies/frame,
        /// 60 m chunks, async build on, NO decor ring by default (games opt into a far horizon by setting
        /// <see cref="DecorRadius"/>). A brisk run (6 m/s) crosses a chunk in ~10 s, far under the per-frame budget.</summary>
        public static StreamerConfig Default => new(LoadRadius: 4, UnloadRadius: 6, MaxLoadsPerFrame: 3, ChunkSize: TerrainChunkRegion.DefaultSize);

        /// <summary>The outer load extent (chunk units): the larger of <see cref="LoadRadius"/> and
        /// <see cref="DecorRadius"/>. Chunks load out to here; those past <see cref="LoadRadius"/> are decor.</summary>
        public int OuterRadius => DecorRadius > LoadRadius ? DecorRadius : LoadRadius;

        /// <summary>The LOD tier table this config picks with (<see cref="TerrainLodConfig.Default"/> when unset).</summary>
        public TerrainLodConfig ResolvedLodConfig => LodConfig ?? TerrainLodConfig.Default;

        /// <summary>This config with async build turned off (the old synchronous build+upload-on-the-frame-thread path).
        /// Handy for editors/tools that want blocking, deterministic loads: <c>StreamerConfig.Default.Synchronous()</c>.</summary>
        public StreamerConfig Synchronous() => this with { Async = false };
    }
}
