namespace KhaozEngine.Terrain
{
    /// <summary>One designed region along the world Z axis: it contributes its BaseHeight and HillAmplitude
    /// where it is dominant, smoothstep-blended with its neighbours across TerrainConfig.BiomeBlend. Outer
    /// bands use +/- infinity for the open edge.</summary>
    public struct BiomeBand
    {
        public float Start;
        public float End;
        public BiomeId Biome;
        public float BaseHeight;
        public float HillAmplitude;
    }
}
