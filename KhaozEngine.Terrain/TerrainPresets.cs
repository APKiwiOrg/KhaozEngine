namespace KhaozEngine.Terrain
{
    /// <summary>Ready-made TerrainConfigs. Clearing reproduces the tools/blender/make_clearing_greybox.py
    /// "forest clearing at a mountain base": gentle meadow, mountains ramping toward +Z, a carved lake basin.
    /// Used as the field parity fixture and a demo/world seed. The greybox's (x, y) map to our (x, z); its
    /// returned Blender-Z is our world height (Y up).</summary>
    public static class TerrainPresets
    {
        public static TerrainConfig Clearing(int seed = 5) => new TerrainConfig
        {
            Seed = seed,
            WaterLevel = -1.2f,
            BiomeBlend = 26f,              // blend window [48-26, 48+26] = [22, 74] == greybox SmoothStep(22, 74, z)
            GentleFrequency = 0.02f,
            GentleAmplitude = 1.5f,
            DetailFrequency = 0.03f,
            DetailOctaves = 4,
            Biomes = new[]
            {
                new BiomeBand { Start = float.NegativeInfinity, End = 48f, Biome = BiomeId.Meadow,    BaseHeight = 0f,  HillAmplitude = 0f },
                new BiomeBand { Start = 48f, End = float.PositiveInfinity, Biome = BiomeId.Mountains, BaseHeight = 34f, HillAmplitude = 22f },
            },
            Features = new ITerrainFeature[]
            {
                new LakeFeature(centerX: -13f, centerZ: -2f, radius: 8f, depth: 3.6f),
            },
        };
    }
}
