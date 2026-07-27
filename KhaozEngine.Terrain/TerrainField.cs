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
        // Volatile, not readonly: SetSculpt swaps the whole snapshot while worker threads sample. Volatile is
        // load-bearing rather than decorative - a plain reference write is atomic but is NOT ordered against the
        // writes that filled the new TerrainSculpt's dictionary, so on a weak memory model (arm64) a reader can
        // observe the new reference before that dictionary is visible and read a half-built one. The volatile
        // write is a release and every read below is an acquire, which is what makes "either the old snapshot or
        // the new one, never a torn state" actually true. Every public sampler reads this field EXACTLY ONCE into
        // a local and threads that local through the private overloads, so one call is one snapshot by
        // construction (see SampleNormal, which used to read it five times per call).
        volatile TerrainSculpt? _sculpt;

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

        /// <summary>Replaces the sculpt layer with a new immutable snapshot, by an atomic reference exchange. A
        /// sampler running concurrently on a worker thread sees either the old snapshot or the new one, never a
        /// torn state, and both are valid terrain: every public sampler takes ONE read of the field and threads
        /// that one snapshot through the whole call. Applies the SAME normalization the constructor does - a null
        /// or empty sculpt stores null, which is what keeps the analytic fast path in <see cref="SampleHeight(float, float)"/>
        /// and the 1 m normal epsilon in <see cref="SampleNormal"/>.
        /// <para>Build the new snapshot with <see cref="TerrainSculpt.With"/>, which shares every unchanged
        /// tile's delta array by reference, so a swap costs O(tile count) and copies no deltas. A chunk build
        /// already running when the swap lands carries the pre-swap terrain, which the caller corrects by
        /// invalidating that chunk (<see cref="TerrainStreamer.Invalidate(RectArea)"/>) after the swap - a
        /// bounded, self-correcting outcome rather than a torn read.</para></summary>
        public void SetSculpt(TerrainSculpt? sculpt) => _sculpt = sculpt is { IsEmpty: false } ? sculpt : null;

        /// <summary>The one source of truth for ground height at a world point. Folds biome shape, base
        /// coordinate-hash noise, then each feature in order, then adds the authored sculpt delta when a
        /// sculpt layer is attached. Stateless in (x,z,seed) and the sculpt data.</summary>
        public float SampleHeight(float x, float z) => SampleHeight(x, z, _sculpt);

        // One snapshot, threaded in by the public entry point that read the field. Never reads _sculpt itself.
        float SampleHeight(float x, float z, TerrainSculpt? sculpt)
        {
            var shape = ShapeAt(z);
            float gentle = _cfg.GentleAmplitude * TerrainNoise.Fbm(x * _cfg.GentleFrequency, z * _cfg.GentleFrequency, _cfg.Seed);
            float detail = TerrainNoise.Turbulence(x * _cfg.DetailFrequency, z * _cfg.DetailFrequency, _cfg.Seed, _cfg.DetailOctaves);
            float h = shape.baseHeight + gentle + shape.hillAmp * detail;

            var feats = _cfg.Features;
            if (feats != null)
                for (int i = 0; i < feats.Length; i++)
                    h = feats[i].Apply(x, z, h);

            if (sculpt != null)
                h += sculpt.SampleDelta(x, z);
            return h;
        }

        /// <summary>Surface normal via central finite difference over the composited height. The step is 1 m
        /// on the analytic fast path (no sculpt), and the sculpt cell size when a sculpt layer is attached, so
        /// slope gates read the sculpted surface rather than the analytic one. Flat ground returns +Y.
        /// <para>The sculpt snapshot is read ONCE here and threaded through all four height samples AND the
        /// epsilon. Re-reading it per sample would let a concurrent <see cref="SetSculpt"/> build a normal out of
        /// two different snapshots, or pair one snapshot's epsilon with another's heights, which is not "the old
        /// one or the new one" but a third surface belonging to neither.</para></summary>
        public Vector3 SampleNormal(float x, float z)
        {
            TerrainSculpt? sculpt = _sculpt;
            float eps = sculpt is null ? 1f : sculpt.CellSize;
            float hxp = SampleHeight(x + eps, z, sculpt), hxm = SampleHeight(x - eps, z, sculpt);
            float hzp = SampleHeight(x, z + eps, sculpt), hzm = SampleHeight(x, z - eps, sculpt);
            var n = new Vector3(-(hxp - hxm) / (2f * eps), 1f, -(hzp - hzm) / (2f * eps));
            return Vector3.Normalize(n);
        }

        /// <summary>The dominant biome at the world point (from the band blend).</summary>
        public BiomeId SampleBiome(float x, float z) => ShapeAt(z).biome;
    }
}
