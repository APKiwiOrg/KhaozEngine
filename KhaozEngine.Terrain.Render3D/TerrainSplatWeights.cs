using System;

namespace KhaozEngine.Terrain
{
    /// <summary>Per-vertex terrain surface mix: five normalized weights baked from height, slope and biome by the
    /// chunk builder. The weights pick the material: the splat pipeline blends five textured layers by them
    /// (<see cref="TerrainSplatPacking"/>, <see cref="TerrainLayeredMaterial"/>), and a chunk loaded without a material
    /// renders them as the <see cref="TerrainRamp"/> vertex colour. Render-data only.
    /// <para>The physical rules come first: steep ground is rock, ground near or below the water is sand and ground
    /// above the snow line is snow. What they leave over is grass with a little mid-slope dirt, and the biome tilts
    /// only that grass share (<see cref="BiomeSplatBias"/> carries the table and the reasoning). Meadow has no tilt, so
    /// an all-Meadow world bakes exactly what it did before biomes counted.</para></summary>
    public struct TerrainSplatWeights
    {
        public float Grass, Dirt, Rock, Sand, Snow;

        /// <summary>Bakes a normalized weight set for one discrete biome. slope01 = 1 - normal.Y clamped (0 flat,
        /// 1 vertical). Steep -> rock, near/below water -> sand, above snowLine -> snow, otherwise grass with a little
        /// mid-slope dirt, and then <paramref name="biome"/> moves part of that grass to its own channels (Desert to
        /// sand, Snow to snow, Marsh and Forest to dirt, Mountains to rock and dirt). The chunk builder calls
        /// <see cref="FromBlend"/> instead, which fades the tilt across a band boundary. This overload gives the same
        /// result wherever one biome has the whole share.</summary>
        public static TerrainSplatWeights From(float height, float slope01, BiomeId biome, float waterLevel, float snowLine = 60f)
            => Bake(height, slope01, BiomeSplatBias.For(biome), waterLevel, snowLine);

        /// <summary>Bakes a normalized weight set with each biome's tilt weighted by its share at the vertex, which is
        /// what the chunk builder uses (<see cref="TerrainField.SampleBiomeWeights"/> supplies the shares). The
        /// shares change smoothly across a band's blend window, so the ground mix does too, where a per-vertex
        /// <see cref="From"/> with the dominant biome would switch along one triangle row. Bit-identical to
        /// <see cref="From"/> when one biome holds the whole share. A <c>default</c> <see cref="BiomeWeights"/> tilts
        /// nothing, like Meadow.</summary>
        public static TerrainSplatWeights FromBlend(float height, float slope01, in BiomeWeights biomes, float waterLevel, float snowLine = 60f)
            => Bake(height, slope01, BiomeSplatBias.Blend(biomes), waterLevel, snowLine);

        static TerrainSplatWeights Bake(float height, float slope01, in BiomeSplatBias bias, float waterLevel, float snowLine)
        {
            slope01 = Math.Clamp(slope01, 0f, 1f);
            float rock = TerrainNoise.SmoothStep(0.45f, 0.85f, slope01);          // steepness -> rock
            float snow = (1f - rock) * TerrainNoise.SmoothStep(snowLine - 12f, snowLine + 8f, height);
            float sand = (1f - rock) * (1f - snow) * (1f - TerrainNoise.SmoothStep(waterLevel + 0.2f, waterLevel + 2.5f, height));
            float dirt = (1f - rock) * (1f - snow) * (1f - sand) * TerrainNoise.SmoothStep(0.15f, 0.5f, slope01) * 0.5f;
            float grass = MathF.Max(0f, 1f - rock - snow - sand - dirt);

            // The biome tilt moves part of the grass share and nothing else, so every physical rule above keeps its
            // weight. A zero tilt (Meadow) skips this block, which keeps that path bit-identical to the pre-biome mix.
            if (!bias.IsZero)
            {
                float moved = grass;
                dirt += moved * bias.Dirt;
                rock += moved * bias.Rock;
                sand += moved * bias.Sand;
                snow += moved * bias.Snow;
                grass = MathF.Max(0f, moved * (1f - bias.Total));
            }

            var w = new TerrainSplatWeights { Grass = grass, Dirt = dirt, Rock = rock, Sand = sand, Snow = snow };
            return w.Normalized();
        }

        /// <summary>This set scaled so the five weights sum to 1. A set summing to ~0 has no mix to preserve, so
        /// grass is forced to 1 rather than dividing by nothing. The splat pipeline
        /// packs the four leading weights into vertex colour and reconstructs snow in the shader as <c>1 - sum</c>
        /// (see <see cref="TerrainSplatPacking"/>), so an unnormalized set renders as snow bleeding in.
        /// <see cref="From"/> and <see cref="FromBlend"/> already normalize. This is for a consumer splat rule
        /// (<see cref="TerrainSplatContext"/>) that adjusts a weight and needs the invariant back.</summary>
        public readonly TerrainSplatWeights Normalized()
        {
            var w = this;
            float sum = w.Grass + w.Dirt + w.Rock + w.Sand + w.Snow;
            if (sum > 1e-6f)
            {
                w.Grass /= sum; w.Dirt /= sum; w.Rock /= sum; w.Sand /= sum; w.Snow /= sum;
            }
            else w.Grass = 1f;
            return w;
        }
    }
}
