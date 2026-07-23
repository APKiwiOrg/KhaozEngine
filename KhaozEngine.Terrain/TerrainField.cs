using System;
using System.Numerics;

namespace KhaozEngine.Terrain
{
    /// <summary>
    /// The terrain height field: the single source of truth for ground height. SampleHeight folds the
    /// analytic layers in order - biome shape (designed regions, smoothstep-blended), base coordinate-hash
    /// noise, then an ordered feature list (lakes/ridges/flatten) - and then adds the authored sculpt delta
    /// when a non-empty <see cref="TerrainSculpt"/> is attached. Stateless: the height at (x,z) depends only
    /// on (x,z,seed) and the sculpt data, so server and client agree and streamed chunks line up regardless
    /// of load order.
    /// </summary>
    public sealed class TerrainField
    {
        readonly TerrainConfig _cfg;
        readonly BiomeBand[] _bands;
        readonly TerrainSculpt? _sculpt;

        public TerrainField(TerrainConfig config) : this(config, null) { }

        /// <summary>Builds the field over an optional authored sculpt layer. A null or empty
        /// <paramref name="sculpt"/> keeps the pure-analytic fast path (heights and normals identical to the
        /// no-sculpt field), so unsculpted zones pay nothing.</summary>
        public TerrainField(TerrainConfig config, TerrainSculpt? sculpt)
        {
            _cfg = config ?? throw new ArgumentNullException(nameof(config));
            _bands = (config.Biomes is { Length: > 0 })
                ? config.Biomes
                : new[] { new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, Biome = BiomeId.Meadow, BaseHeight = 0f, HillAmplitude = 0f } };
            _sculpt = sculpt is { IsEmpty: false } ? sculpt : null;
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

        /// <summary>The one source of truth for ground height at a world point. Folds biome shape, base
        /// coordinate-hash noise, then each feature in order, then adds the authored sculpt delta when a
        /// sculpt layer is attached. Stateless in (x,z,seed) and the sculpt data.</summary>
        public float SampleHeight(float x, float z)
        {
            var shape = ShapeAt(z);
            float gentle = _cfg.GentleAmplitude * TerrainNoise.Fbm(x * _cfg.GentleFrequency, z * _cfg.GentleFrequency, _cfg.Seed);
            float detail = TerrainNoise.Turbulence(x * _cfg.DetailFrequency, z * _cfg.DetailFrequency, _cfg.Seed, _cfg.DetailOctaves);
            float h = shape.baseHeight + gentle + shape.hillAmp * detail;

            var feats = _cfg.Features;
            if (feats != null)
                for (int i = 0; i < feats.Length; i++)
                    h = feats[i].Apply(x, z, h);

            if (_sculpt != null)
                h += _sculpt.SampleDelta(x, z);
            return h;
        }

        /// <summary>Surface normal via central finite difference over the composited height. The step is 1 m
        /// on the analytic fast path (no sculpt), and the sculpt cell size when a sculpt layer is attached, so
        /// slope gates read the sculpted surface rather than the analytic one. Flat ground returns +Y.</summary>
        public Vector3 SampleNormal(float x, float z)
        {
            float eps = _sculpt is null ? 1f : _sculpt.CellSize;
            float hxp = SampleHeight(x + eps, z), hxm = SampleHeight(x - eps, z);
            float hzp = SampleHeight(x, z + eps), hzm = SampleHeight(x, z - eps);
            var n = new Vector3(-(hxp - hxm) / (2f * eps), 1f, -(hzp - hzm) / (2f * eps));
            return Vector3.Normalize(n);
        }

        /// <summary>The dominant biome at the world point (from the band blend).</summary>
        public BiomeId SampleBiome(float x, float z) => ShapeAt(z).biome;
    }
}
