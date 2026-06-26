namespace KhaozEngine.Terrain
{
    /// <summary>Authoring inputs for a TerrainField. Defaults give a single gentle meadow band; supply Biomes
    /// for designed regions and Features for lakes/ridges/flattened hubs.</summary>
    public sealed class TerrainConfig
    {
        public int Seed = 1;
        public float WaterLevel = 0f;
        /// <summary>Smoothstep blend half-width (metres) at biome-band boundaries.</summary>
        public float BiomeBlend = 24f;
        /// <summary>Low-frequency ground roll applied everywhere.</summary>
        public float GentleFrequency = 0.02f;
        public float GentleAmplitude = 1.5f;
        /// <summary>Detail octave scaled by the dominant band's HillAmplitude (non-negative turbulence: only raises).</summary>
        public float DetailFrequency = 0.03f;
        public int DetailOctaves = 4;
        public BiomeBand[]? Biomes;
        public ITerrainFeature[]? Features;
    }
}
