namespace KhaozEngine.Terrain
{
    /// <summary>Which residency ring a streamed chunk sits in, chosen by the <see cref="TerrainStreamer"/> from the
    /// chunk's distance and handed to the sink so it knows how much of a chunk to build.
    /// <list type="bullet">
    /// <item><see cref="Gameplay"/>: a full chunk the simulation touches. The sink scatters props, registers their
    /// static bodies, and (when opted in) registers the terrain surface collider.</item>
    /// <item><see cref="Decor"/>: a render-only chunk between the gameplay radius and the far/decor radius. The sink
    /// skips scatter, prop colliders, dynamics, and terrain collision entirely - it is scenery, never simulated -
    /// so seeing the far field costs only the (coarse) terrain mesh.</item>
    /// </list>
    /// A chunk upgrades <see cref="Decor"/> -> <see cref="Gameplay"/> as the player approaches (gaining scatter and
    /// colliders) and downgrades back on retreat (shedding them), through the same re-LOD path a tier change uses.</summary>
    public enum ChunkRing
    {
        /// <summary>A simulated chunk inside the gameplay radius: full scatter, prop colliders, and terrain collision.</summary>
        Gameplay = 0,

        /// <summary>A render-only chunk in the far/decor ring: terrain mesh only, no scatter or physics.</summary>
        Decor = 1,
    }
}
