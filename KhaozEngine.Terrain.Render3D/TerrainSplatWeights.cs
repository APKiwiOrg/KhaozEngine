using System;

namespace KhaozEngine.Terrain
{
    /// <summary>Per-vertex terrain surface mix: five normalized weights baked from height + slope + biome by the
    /// chunk builder. The current slice renders these as a vertex-colour ramp (TerrainRamp); the weights are
    /// plumbed now so the later PBR splat-TEXTURE sub-project is a drop-in (it samples albedo/normal per channel
    /// instead of blending palette colours). Render-data only.</summary>
    public struct TerrainSplatWeights
    {
        public float Grass, Dirt, Rock, Sand, Snow;

        /// <summary>Bakes a normalized weight set. slope01 = 1 - normal.Y clamped (0 flat, 1 vertical). Steep ->
        /// rock; near/below water -> sand; above snowLine -> snow; otherwise grass with a little mid-slope dirt.</summary>
        public static TerrainSplatWeights From(float height, float slope01, BiomeId biome, float waterLevel, float snowLine = 60f)
        {
            slope01 = Math.Clamp(slope01, 0f, 1f);
            float rock = TerrainNoise.SmoothStep(0.45f, 0.85f, slope01);          // steepness -> rock
            float snow = (1f - rock) * TerrainNoise.SmoothStep(snowLine - 12f, snowLine + 8f, height);
            float sand = (1f - rock) * (1f - snow) * (1f - TerrainNoise.SmoothStep(waterLevel + 0.2f, waterLevel + 2.5f, height));
            float dirt = (1f - rock) * (1f - snow) * (1f - sand) * TerrainNoise.SmoothStep(0.15f, 0.5f, slope01) * 0.5f;
            float grass = MathF.Max(0f, 1f - rock - snow - sand - dirt);

            var w = new TerrainSplatWeights { Grass = grass, Dirt = dirt, Rock = rock, Sand = sand, Snow = snow };
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
