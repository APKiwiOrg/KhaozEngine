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
    /// index means the same grid resolution on both sides (both default, so the default wiring aligns for free).
    /// <see cref="LodHysteresis"/> is the dead zone (metres) around each tier boundary, so a chunk parked on one does
    /// not re-tier (and rebuild its mesh) on every small move. It damps only a CHANGE: a first load and a viewer that
    /// has not moved pick exactly the stateless tier. 0 restores the undamped behaviour.</para>
    /// <para><see cref="Async"/> (default true): the CPU mesh build runs on a background thread and only the GPU
    /// upload happens on the frame thread. <see cref="MaxLoadsPerFrame"/> then caps how many completed builds are
    /// APPLIED (GPU upload + swap) per <c>Update</c>. The builds themselves are unbudgeted (they run in parallel off
    /// the frame thread). When <see cref="Async"/> is false, or the sink is not an <see cref="IAsyncChunkSink"/>, the
    /// streamer runs the old synchronous path where <see cref="MaxLoadsPerFrame"/> caps build+upload ops done inline.</para>
    /// <para><b>Unloads.</b> <see cref="MaxUnloadsPerFrame"/> caps how many out-of-range chunks are freed per
    /// <c>Update</c>, farthest first, so a ring shift spreads over frames instead of freeing a whole outgoing ring in
    /// one call. The rest are simply reconsidered next frame, which means a chunk that comes back into range before
    /// its turn is never unloaded at all (and so never reloaded). 0 or less opts out and frees everything at once.
    /// <c>UnloadAll</c> is never budgeted: a teleport or world rebuild must free the whole ring on the spot.</para></summary>
    public readonly record struct StreamerConfig(int LoadRadius, int UnloadRadius, int MaxLoadsPerFrame, float ChunkSize, bool Async = true, int DecorRadius = 0, TerrainLodConfig? LodConfig = null, float LodHysteresis = TerrainLodConfig.DefaultHysteresis, int MaxUnloadsPerFrame = StreamerConfig.DefaultMaxUnloadsPerFrame)
    {
        /// <summary>Default per-frame unload budget. A one-chunk ring shift exposes roughly a dozen chunks at the
        /// default radii, so 8 clears an ordinary shift in two frames while still capping the worst case (a long
        /// jump, or a slow frame that skipped several chunk boundaries at once).</summary>
        public const int DefaultMaxUnloadsPerFrame = 8;

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
