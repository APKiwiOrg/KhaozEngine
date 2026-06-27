using System;
using System.Numerics;

namespace KhaozEngine.Terrain
{
    /// <summary>Ready-made TerrainConfigs. Clearing reproduces the tools/blender/make_clearing_greybox.py
    /// "forest clearing at a mountain base": gentle meadow, mountains ramping toward +Z, a carved lake basin.
    /// Used as the field parity fixture and a demo/world seed. The greybox's (x, y) map to our (x, z); its
    /// returned Blender-Z is our world height (Y up). BoundedClearing wraps a meadow in a RimFeature wall for
    /// the first ready-made bounded zone.</summary>
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

        /// <summary>A bounded forest clearing: a single gentle meadow ringed by a RimFeature mountain wall with
        /// one pass to the north (+Z, the road out) and a carved lake. The rim is the diegetic border (un-climbable
        /// once the movement slope gate is wired with TerrainCollision.GroundNormal); pair with a
        /// KhaozEngine.NetWorld.WorldBounds for an authoritative hard stop. The first ready-made bounded zone -
        /// games compose their own (town pads, buildings) on top.</summary>
        public static TerrainConfig BoundedClearing(int seed = 5) => new TerrainConfig
        {
            Seed = seed,
            WaterLevel = -1.2f,
            GentleFrequency = 0.02f,
            GentleAmplitude = 1.0f,
            DetailFrequency = 0.03f,
            DetailOctaves = 4,
            Biomes = new[]
            {
                // one gentle meadow everywhere; the rim provides the mountains (no +Z mountains band here).
                new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, Biome = BiomeId.Meadow, BaseHeight = 0f, HillAmplitude = 1.0f },
            },
            Features = new ITerrainFeature[]
            {
                new LakeFeature(centerX: -12f, centerZ: -4f, radius: 8f, depth: 3.6f),
                new RimFeature(
                    center: Vector2.Zero, innerRadius: 38f, outerRadius: 56f, wallHeight: 30f,
                    ruggedness: 0.3f, seed: seed,
                    passes: new[] { new RimPass(angleRadians: MathF.PI / 2f /* +Z */, halfWidth: 10f, falloff: 6f) }),
            },
        };
    }
}
