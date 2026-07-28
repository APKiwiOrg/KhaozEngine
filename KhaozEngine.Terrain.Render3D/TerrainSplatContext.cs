namespace KhaozEngine.Terrain
{
    /// <summary>Everything one terrain vertex knows, handed to a consumer-supplied splat rule
    /// (<c>Func&lt;TerrainSplatContext, TerrainSplatWeights&gt;</c>) so a game can influence the material mix the
    /// chunk builder bakes. <see cref="Default"/> is the engine's own <see cref="TerrainSplatWeights.From"/> result
    /// for this vertex, so the common rule is "the engine's mix, adjusted", not a reimplementation that drifts from
    /// the engine's tuning the first time <c>From</c> changes.
    /// <para>The motivating case is a second body of water. <c>From</c> derives its sand band from the field's single
    /// <c>WaterLevel</c>, which is the sea, so an inland lake's shoreline bakes as grass running straight into the
    /// water. A rule reads <see cref="WorldX"/>/<see cref="WorldZ"/>/<see cref="Height"/>, decides it is near a lake
    /// edge, and pushes <see cref="TerrainSplatWeights.Sand"/> up. Paths, trampled ground, and biome-specific dirt
    /// are the same shape.</para>
    /// <para><b>Three constraints, all load-bearing.</b></para>
    /// <para><b>1. The rule must be PURE.</b> Same context in, same weights out, forever, on any thread. Chunk
    /// meshes are built per (region, LOD) and cached until unload, and the streamer builds them on background
    /// threads in whatever order the player walks. A rule that reads mutable game state (time of day, a live water
    /// level, a weather flag, an RNG) bakes a chunk differently depending on WHEN it streamed in: two neighbours
    /// loaded seconds apart disagree at their shared edge, and the seam does not heal until something re-LODs or
    /// invalidates them. Everything the rule needs must either be in the context or be immutable data captured when
    /// the rule was built. A world whose splat genuinely changes swaps the rule and re-primes the ring, it does not
    /// mutate what an existing rule reads.</para>
    /// <para><b>2. This is a HOT PATH.</b> Called once per vertex of every streamed chunk, on the build thread. That
    /// is thousands of calls per chunk and every chunk the player walks into. No allocation, no locking, no IO, no
    /// LINQ. Pre-bake whatever the rule needs (a lake list, a spatial index) when the rule is constructed.</para>
    /// <para><b>3. Splat is PRESENTATION ONLY.</b> The weights ride in vertex colour and pick which material layers
    /// blend. They do not feed the <c>TerrainField</c>, collision, the map document, or any world-identity hash, so
    /// adopting a rule cannot desync a client from an authoritative server and does not change a saved world. A
    /// server that never builds chunk meshes never runs the rule at all.</para>
    /// <para><b>Return normalized weights.</b> The splat pipeline packs the four leading weights into vertex colour
    /// and reconstructs snow in the shader as <c>1 - sum</c> (see <c>TerrainSplatPacking</c>), so a set summing to
    /// less than 1 shows up as snow bleeding in. <see cref="Default"/> is already normalized; a rule that adjusts a
    /// weight restores the invariant with <see cref="TerrainSplatWeights.Normalized"/>. The engine deliberately does
    /// not renormalize the rule's output: that would be a per-vertex cost paid by every consumer to paper over one
    /// consumer's bug.</para></summary>
    /// <param name="Height">The vertex's world height in metres (the field's sampled height, absolute, not chunk-local).</param>
    /// <param name="Slope01">Steepness as <c>1 - normal.Y</c>, 0 flat and 1 vertical. Same value
    /// <see cref="TerrainSplatWeights.From"/> was given.</param>
    /// <param name="Biome">The field's designed biome at this vertex (<c>TerrainField.SampleBiome</c>).</param>
    /// <param name="WorldX">The vertex's ABSOLUTE world X in metres. Chunk vertices are stored chunk-local, but the
    /// field is sampled at the absolute coordinate and that is what a rule needs to place a feature in the world.</param>
    /// <param name="WorldZ">The vertex's ABSOLUTE world Z in metres.</param>
    /// <param name="Default">What the engine's own rule (<see cref="TerrainSplatWeights.From"/>) baked for this
    /// vertex, normalized. Return it unchanged to defer to the engine for a vertex the rule has no opinion on.</param>
    public readonly record struct TerrainSplatContext(
        float Height,
        float Slope01,
        BiomeId Biome,
        float WorldX,
        float WorldZ,
        TerrainSplatWeights Default);
}
