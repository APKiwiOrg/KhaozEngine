using System;
using System.Numerics;

namespace KhaozEngine.Terrain
{
    /// <summary>
    /// The analytic terrain height field: the single source of truth for ground height. SampleHeight folds
    /// three layers in order - biome shape (designed regions, smoothstep-blended), base coordinate-hash noise,
    /// then an ordered feature list (lakes/ridges/flatten). Stateless: the height at (x,z) depends only on
    /// (x,z,seed), so server and client agree and streamed chunks line up regardless of load order.
    /// </summary>
    public sealed class TerrainField
    {
        readonly TerrainConfig _cfg;
        readonly BiomeBand[] _bands;

        public TerrainField(TerrainConfig config)
        {
            _cfg = config ?? throw new ArgumentNullException(nameof(config));
            _bands = (config.Biomes is { Length: > 0 })
                ? config.Biomes
                : new[] { new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, Biome = BiomeId.Meadow, BaseHeight = 0f, HillAmplitude = 0f } };
        }

        public float WaterLevel => _cfg.WaterLevel;

        /// <summary>Blends the biome bands at world Z: normalized smoothstep weights -> (baseHeight, hillAmp);
        /// biome = argmax weight. Continuous everywhere.</summary>
        internal (float baseHeight, float hillAmp, BiomeId biome) ShapeAt(float z)
        {
            float blend = MathF.Max(1e-3f, _cfg.BiomeBlend);
            float wSum = 0f, baseH = 0f, hill = 0f, bestW = -1f;
            BiomeId best = _bands[0].Biome;
            for (int i = 0; i < _bands.Length; i++)
            {
                ref readonly BiomeBand b = ref _bands[i];
                float rise = float.IsNegativeInfinity(b.Start) ? 1f : TerrainNoise.SmoothStep(b.Start - blend, b.Start + blend, z);
                float fall = float.IsPositiveInfinity(b.End) ? 1f : 1f - TerrainNoise.SmoothStep(b.End - blend, b.End + blend, z);
                float w = rise * fall;
                wSum += w;
                baseH += w * b.BaseHeight;
                hill += w * b.HillAmplitude;
                if (w > bestW) { bestW = w; best = b.Biome; }
            }
            if (wSum > 1e-6f) { baseH /= wSum; hill /= wSum; }
            return (baseH, hill, best);
        }
    }
}
