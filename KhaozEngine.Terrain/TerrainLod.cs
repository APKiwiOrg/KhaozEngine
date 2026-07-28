namespace KhaozEngine.Terrain
{
    /// <summary>Distance-to-LOD mapping for chunked terrain, kept as a thin facade over
    /// <see cref="TerrainLodConfig.Default"/> so callers that do not thread a custom config still get the default
    /// tiers. <see cref="PickLod(float)"/> chooses a tier from camera distance (near = dense, far = coarse);
    /// <see cref="ResolutionFor"/> gives that tier's grid resolution. The tiers themselves (and adding far tiers or a
    /// game-specific table) live in <see cref="TerrainLodConfig"/>; wire a custom one through
    /// <see cref="StreamerConfig.LodConfig"/> and the <c>Scene3DChunkSink</c>. Which chunks exist and when they
    /// rebuild is the streaming sub-project, not this one.</summary>
    public static class TerrainLod
    {
        /// <summary>The default tier table (legacy 64/32/16 at 80 m/200 m plus the coarser far tiers). Shorthand for
        /// <see cref="TerrainLodConfig.Default"/>.</summary>
        public static TerrainLodConfig Default => TerrainLodConfig.Default;

        /// <summary>The first tier boundary (metres) in the default config. Retained for back-compat.</summary>
        public const float NearMax = 80f;

        /// <summary>The second tier boundary (metres) in the default config. Retained for back-compat.</summary>
        public const float MidMax = 200f;

        /// <summary>Number of tiers in the default config.</summary>
        public static int TierCount => TerrainLodConfig.Default.TierCount;

        /// <summary>Tier index for a camera distance, from the default config. Monotone in distance.</summary>
        public static int PickLod(float distance) => TerrainLodConfig.Default.PickLod(distance);

        /// <summary>Tier index for a camera distance with a dead zone around each boundary, from the default config.
        /// Pass -1 for <paramref name="currentLod"/> when the chunk has no tier yet. See
        /// <see cref="TerrainLodConfig.PickLod(float, int, float)"/>.</summary>
        public static int PickLod(float distance, int currentLod, float hysteresis)
            => TerrainLodConfig.Default.PickLod(distance, currentLod, hysteresis);

        /// <summary>Grid resolution (segments per chunk edge) for a tier, from the default config.</summary>
        public static int ResolutionFor(int lod) => TerrainLodConfig.Default.ResolutionFor(lod);
    }
}
