using System;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary><see cref="TerrainField.SampleBiomeWeights"/> and <see cref="BiomeWeights"/>: the continuous biome
    /// signal the default splat blends its per-biome tilt over. The contract is that it sums to 1, moves smoothly
    /// across a band boundary, and names the same dominant biome <see cref="TerrainField.SampleBiome"/> does, so the
    /// splat rule's <c>ctx.Biome</c> did not change when the default mix started reading shares.</summary>
    public class BiomeWeightsTests
    {
        static float Sum(in BiomeWeights w)
        {
            float s = 0f;
            foreach (BiomeId b in Enum.GetValues<BiomeId>()) s += w[b];
            return s;
        }

        static TerrainField Bands(float blend, params BiomeBand[] bands) => new(new TerrainConfig
        {
            Seed = 3,
            BiomeBlend = blend,
            Biomes = bands,
        });

        static BiomeBand Band(float start, float end, BiomeId biome) =>
            new() { Start = start, End = end, Biome = biome, BaseHeight = 0f, HillAmplitude = 0f };

        [Fact]
        public void Every_BiomeId_has_a_slot()
        {
            // Adding a biome to the enum without a slot here would silently give it no share, and so no splat tilt.
            BiomeId[] all = Enum.GetValues<BiomeId>();
            Assert.Equal(BiomeWeights.Count, all.Length);
            foreach (BiomeId b in all)
            {
                BiomeWeights single = BiomeWeights.Single(b);
                Assert.Equal(b, single.Dominant);
                foreach (BiomeId other in all) Assert.Equal(other == b ? 1f : 0f, single[other]);
            }
        }

        [Fact]
        public void An_id_outside_the_enum_has_no_share()
        {
            var bogus = (BiomeId)200;
            Assert.Equal(0f, BiomeWeights.Single(BiomeId.Desert)[bogus]);
            BiomeWeights single = BiomeWeights.Single(bogus);
            Assert.Equal(bogus, single.Dominant);
            Assert.Equal(0f, Sum(single));
        }

        [Fact]
        public void Shares_sum_to_one_and_Dominant_matches_SampleBiome_everywhere()
        {
            // Three fields: the Clearing preset, a six-band strip with overlapping blend windows, and the default
            // single-Meadow config. Dominant must be SampleBiome bit for bit, since the builder hands it to the rule.
            TerrainField[] fields =
            {
                new(TerrainPresets.Clearing()),
                Bands(20f,
                    Band(float.NegativeInfinity, 30f, BiomeId.Meadow), Band(30f, 60f, BiomeId.Forest),
                    Band(60f, 90f, BiomeId.Marsh), Band(90f, 120f, BiomeId.Mountains),
                    Band(120f, 150f, BiomeId.Desert), Band(150f, float.PositiveInfinity, BiomeId.Snow)),
                new(new TerrainConfig()),
            };
            foreach (TerrainField field in fields)
                for (float z = -100f; z <= 250f; z += 0.37f)
                {
                    BiomeWeights w = field.SampleBiomeWeights(5f, z);
                    Assert.Equal(field.SampleBiome(5f, z), w.Dominant);
                    Assert.Equal(1f, Sum(w), 5);
                    foreach (BiomeId b in Enum.GetValues<BiomeId>()) Assert.InRange(w[b], 0f, 1f);
                }
        }

        [Fact]
        public void A_single_band_field_is_all_of_that_biome()
        {
            TerrainField field = Bands(24f, Band(float.NegativeInfinity, float.PositiveInfinity, BiomeId.Desert));
            for (float z = -500f; z <= 500f; z += 13.1f)
            {
                BiomeWeights w = field.SampleBiomeWeights(0f, z);
                Assert.Equal(BiomeId.Desert, w.Dominant);
                Assert.Equal(1f, w[BiomeId.Desert]);   // exactly: w / w
                Assert.Equal(0f, w[BiomeId.Meadow]);
            }
        }

        [Fact]
        public void Shares_move_smoothly_across_a_boundary_where_SampleBiome_switches()
        {
            TerrainField field = Bands(12f,
                Band(float.NegativeInfinity, 0f, BiomeId.Meadow), Band(0f, float.PositiveInfinity, BiomeId.Snow));
            const float step = 0.25f;
            BiomeWeights prev = field.SampleBiomeWeights(0f, -30f);
            Assert.Equal(1f, prev[BiomeId.Meadow]);
            float worst = 0f;
            for (float z = -30f + step; z <= 30f; z += step)
            {
                BiomeWeights w = field.SampleBiomeWeights(0f, z);
                worst = MathF.Max(worst, MathF.Abs(w[BiomeId.Snow] - prev[BiomeId.Snow]));
                prev = w;
            }
            Assert.Equal(1f, prev[BiomeId.Snow]);
            // Smoothstep over a 24 m window peaks at a 1.5 / 24 per-metre slope, so a quarter-metre step moves the
            // share by under 0.016. The dominant biome, by contrast, flips from Meadow to Snow in a single step.
            Assert.True(worst < 0.02f, $"share jumped by {worst} in one {step} m step");
            Assert.Equal(BiomeId.Meadow, field.SampleBiome(0f, -0.1f));
            Assert.Equal(BiomeId.Snow, field.SampleBiome(0f, 0.1f));
        }

        [Fact]
        public void A_point_no_band_covers_is_all_of_the_SampleBiome_fallback()
        {
            // A gap wider than the blend window leaves every band weight at zero. SampleBiome then names the first
            // band, and the shares follow it rather than dividing by nothing.
            TerrainField field = Bands(2f, Band(-100f, -50f, BiomeId.Forest), Band(50f, 100f, BiomeId.Desert));
            BiomeWeights w = field.SampleBiomeWeights(0f, 0f);
            Assert.Equal(field.SampleBiome(0f, 0f), w.Dominant);
            Assert.Equal(1f, w[w.Dominant]);
            Assert.Equal(1f, Sum(w));
        }
    }
}
