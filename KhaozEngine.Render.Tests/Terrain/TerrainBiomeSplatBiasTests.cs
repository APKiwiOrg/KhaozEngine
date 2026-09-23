using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>The per-biome tilt on the default splat mix (issue #376). Headless: the chunk builder is CPU only.
    /// <para>The load-bearing test is the Meadow one. Meadow is the default biome, so a world that never set a biome
    /// must bake exactly what it baked before biomes counted. That is asserted against a verbatim copy of the old
    /// formula over a grid, bit for bit, not against a golden that would also pass if both sides drifted.</para></summary>
    public class TerrainBiomeSplatBiasTests
    {
        static readonly BiomeId[] AllBiomes = Enum.GetValues<BiomeId>();
        static readonly float[] WaterLevels = { -1.2f, 0f, 3.5f };
        static readonly float[] SnowLines = { 40f, 60f };

        /// <summary>The default mix exactly as it stood before the biome tilt, copied verbatim. Never edit this to
        /// follow the engine: it is the fixed reference the Meadow path has to keep matching.</summary>
        static TerrainSplatWeights LegacyFrom(float height, float slope01, float waterLevel, float snowLine)
        {
            slope01 = Math.Clamp(slope01, 0f, 1f);
            float rock = TerrainNoise.SmoothStep(0.45f, 0.85f, slope01);
            float snow = (1f - rock) * TerrainNoise.SmoothStep(snowLine - 12f, snowLine + 8f, height);
            float sand = (1f - rock) * (1f - snow) * (1f - TerrainNoise.SmoothStep(waterLevel + 0.2f, waterLevel + 2.5f, height));
            float dirt = (1f - rock) * (1f - snow) * (1f - sand) * TerrainNoise.SmoothStep(0.15f, 0.5f, slope01) * 0.5f;
            float grass = MathF.Max(0f, 1f - rock - snow - sand - dirt);
            var w = new TerrainSplatWeights { Grass = grass, Dirt = dirt, Rock = rock, Sand = sand, Snow = snow };
            float sum = w.Grass + w.Dirt + w.Rock + w.Sand + w.Snow;
            if (sum > 1e-6f) { w.Grass /= sum; w.Dirt /= sum; w.Rock /= sum; w.Sand /= sum; w.Snow /= sum; }
            else w.Grass = 1f;
            return w;
        }

        /// <summary>Heights from below the water to above the snow line, slopes flat to vertical (plus out-of-range
        /// values the clamp has to absorb), three water levels and two snow lines.</summary>
        static IEnumerable<(float h, float slope, float water, float snowLine)> Grid()
        {
            foreach (float water in WaterLevels)
                foreach (float snowLine in SnowLines)
                    for (float h = -6f; h <= 90f; h += 0.73f)
                        for (float slope = -0.1f; slope <= 1.1f; slope += 0.037f)
                            yield return (h, slope, water, snowLine);
        }

        static void AssertBitIdentical(in TerrainSplatWeights expected, in TerrainSplatWeights actual, string where)
        {
            Assert.True(BitConverter.SingleToInt32Bits(expected.Grass) == BitConverter.SingleToInt32Bits(actual.Grass), $"{where}: grass {expected.Grass} != {actual.Grass}");
            Assert.True(BitConverter.SingleToInt32Bits(expected.Dirt) == BitConverter.SingleToInt32Bits(actual.Dirt), $"{where}: dirt {expected.Dirt} != {actual.Dirt}");
            Assert.True(BitConverter.SingleToInt32Bits(expected.Rock) == BitConverter.SingleToInt32Bits(actual.Rock), $"{where}: rock {expected.Rock} != {actual.Rock}");
            Assert.True(BitConverter.SingleToInt32Bits(expected.Sand) == BitConverter.SingleToInt32Bits(actual.Sand), $"{where}: sand {expected.Sand} != {actual.Sand}");
            Assert.True(BitConverter.SingleToInt32Bits(expected.Snow) == BitConverter.SingleToInt32Bits(actual.Snow), $"{where}: snow {expected.Snow} != {actual.Snow}");
        }

        static float Sum(in TerrainSplatWeights w) => w.Grass + w.Dirt + w.Rock + w.Sand + w.Snow;

        [Fact]
        public void Meadow_is_bit_identical_to_the_pre_biome_formula()
        {
            int points = 0;
            foreach (var (h, slope, water, snowLine) in Grid())
            {
                string where = $"h={h} slope={slope} water={water} snowLine={snowLine}";
                TerrainSplatWeights legacy = LegacyFrom(h, slope, water, snowLine);
                AssertBitIdentical(legacy, TerrainSplatWeights.From(h, slope, BiomeId.Meadow, water, snowLine), $"From(Meadow) {where}");
                AssertBitIdentical(legacy, TerrainSplatWeights.FromBlend(h, slope, BiomeWeights.Single(BiomeId.Meadow), water, snowLine), $"FromBlend(Meadow) {where}");
                AssertBitIdentical(legacy, TerrainSplatWeights.FromBlend(h, slope, default, water, snowLine), $"FromBlend(default) {where}");
                points++;
            }
            Assert.True(points > 10_000, $"the grid only covered {points} points");
        }

        [Fact]
        public void An_all_Meadow_world_bakes_bit_identical_chunks()
        {
            // The world-level form of the Meadow guarantee, through the real builder: BoundedClearing is one Meadow
            // band everywhere with a rim wall, a lake, and hills, so it exercises rock, sand, dirt and grass.
            var field = new TerrainField(TerrainPresets.BoundedClearing());
            var region = new TerrainChunkRegion { OriginX = -40f, OriginZ = -40f, Size = 80f };
            TerrainChunkMesh chunk = TerrainChunkBuilder.Build(field, region, lod: 1);
            for (int i = 0; i < chunk.SurfaceVertexCount; i++)
            {
                Vector3 p = chunk.Mesh.Vertices[i].Position;
                float x = p.X + region.OriginX, z = p.Z + region.OriginZ;
                TerrainSplatWeights legacy = LegacyFrom(field.SampleHeight(x, z), 1f - field.SampleNormal(x, z).Y, field.WaterLevel, 60f);
                AssertBitIdentical(legacy, chunk.Splat[i], $"vertex {i}");
            }
        }

        [Theory]
        [InlineData(BiomeId.Forest, 0.70f, 0.30f, 0f, 0f, 0f)]
        [InlineData(BiomeId.Marsh, 0.50f, 0.50f, 0f, 0f, 0f)]
        [InlineData(BiomeId.Mountains, 0.55f, 0.15f, 0.30f, 0f, 0f)]
        [InlineData(BiomeId.Desert, 0.05f, 0.15f, 0f, 0.80f, 0f)]
        [InlineData(BiomeId.Snow, 0.15f, 0f, 0f, 0f, 0.85f)]
        [InlineData(BiomeId.Meadow, 1f, 0f, 0f, 0f, 0f)]
        public void Each_biome_tilts_open_flat_ground_toward_its_own_channels(BiomeId biome, float grass, float dirt, float rock, float sand, float snow)
        {
            // Flat, well above the water, well below the snow line: the physical rules leave the whole vertex to grass,
            // so the result is the biome's tilt table itself.
            TerrainSplatWeights w = TerrainSplatWeights.From(height: 20f, slope01: 0f, biome, waterLevel: 0f, snowLine: 60f);
            Assert.Equal(grass, w.Grass, 5);
            Assert.Equal(dirt, w.Dirt, 5);
            Assert.Equal(rock, w.Rock, 5);
            Assert.Equal(sand, w.Sand, 5);
            Assert.Equal(snow, w.Snow, 5);
        }

        [Fact]
        public void The_physical_rules_still_win_for_every_biome()
        {
            foreach (BiomeId biome in AllBiomes)
            {
                // Steep ground is rock, ground below the water is shore sand, ground above the snow line is snow.
                Assert.True(TerrainSplatWeights.From(20f, 0.9f, biome, 0f).Rock >= 0.9999f, $"{biome}: cliff is not rock");
                Assert.True(TerrainSplatWeights.From(-1f, 0.05f, biome, 0f).Sand >= 0.9999f, $"{biome}: seabed is not sand");
                Assert.True(TerrainSplatWeights.From(75f, 0.05f, biome, 0f, snowLine: 60f).Snow >= 0.9999f, $"{biome}: peak is not snow");
            }
        }

        [Fact]
        public void A_biome_only_ever_moves_grass_and_never_unseats_a_physical_channel()
        {
            // Over the whole grid: relative to the untilted (Meadow) mix, a biome may only take from grass. Rock, sand,
            // snow and slope dirt never lose weight, and a physical channel holding the majority stays the largest.
            foreach (var (h, slope, water, snowLine) in Grid())
            {
                TerrainSplatWeights meadow = TerrainSplatWeights.From(h, slope, BiomeId.Meadow, water, snowLine);
                foreach (BiomeId biome in AllBiomes)
                {
                    TerrainSplatWeights w = TerrainSplatWeights.From(h, slope, biome, water, snowLine);
                    string where = $"{biome} h={h} slope={slope} water={water} snowLine={snowLine}";
                    const float eps = 1e-5f;
                    Assert.True(w.Grass <= meadow.Grass + eps, $"{where}: grass grew");
                    Assert.True(w.Dirt >= meadow.Dirt - eps, $"{where}: dirt shrank");
                    Assert.True(w.Rock >= meadow.Rock - eps, $"{where}: rock shrank");
                    Assert.True(w.Sand >= meadow.Sand - eps, $"{where}: sand shrank");
                    Assert.True(w.Snow >= meadow.Snow - eps, $"{where}: snow shrank");
                    AssertMajorityHolds(meadow.Rock, w.Rock, w, where, "rock");
                    AssertMajorityHolds(meadow.Sand, w.Sand, w, where, "sand");
                    AssertMajorityHolds(meadow.Snow, w.Snow, w, where, "snow");
                }
            }
        }

        static void AssertMajorityHolds(float physical, float tilted, in TerrainSplatWeights w, string where, string channel)
        {
            if (physical <= 0.5f) return;
            float largest = MathF.Max(w.Grass, MathF.Max(w.Dirt, MathF.Max(w.Rock, MathF.Max(w.Sand, w.Snow))));
            Assert.True(tilted >= largest, $"{where}: {channel} held {physical} untilted but is no longer the largest channel");
        }

        [Fact]
        public void Every_biome_stays_normalized_and_survives_the_four_channel_packing()
        {
            // The splat shader rebuilds snow as 1 - (grass + dirt + rock + sand), so a tilted set that does not sum to
            // 1 renders as snow bleeding in. Checked through the real pack and unpack.
            foreach (var (h, slope, water, snowLine) in Grid())
                foreach (BiomeId biome in AllBiomes)
                {
                    TerrainSplatWeights w = TerrainSplatWeights.From(h, slope, biome, water, snowLine);
                    string where = $"{biome} h={h} slope={slope}";
                    Assert.True(MathF.Abs(Sum(w) - 1f) < 1e-5f, $"{where}: sum {Sum(w)}");
                    Assert.True(w.Grass >= 0f && w.Dirt >= 0f && w.Rock >= 0f && w.Sand >= 0f && w.Snow >= 0f, $"{where}: negative weight");
                    var (_, _, _, _, unpackedSnow) = SplatMath.UnpackWeights(TerrainSplatPacking.Pack(w));
                    Assert.True(MathF.Abs(unpackedSnow - w.Snow) < 1e-5f, $"{where}: shader snow {unpackedSnow} != {w.Snow}");
                }
        }

        [Fact]
        public void FromBlend_of_one_biome_is_bit_identical_to_From()
        {
            foreach (var (h, slope, water, snowLine) in Grid())
                foreach (BiomeId biome in AllBiomes)
                    AssertBitIdentical(TerrainSplatWeights.From(h, slope, biome, water, snowLine),
                        TerrainSplatWeights.FromBlend(h, slope, BiomeWeights.Single(biome), water, snowLine),
                        $"{biome} h={h} slope={slope} water={water} snowLine={snowLine}");
        }

        static TerrainField FlatBoundary(BiomeId below, BiomeId above, float blend) => new(new TerrainConfig
        {
            Seed = 9,
            WaterLevel = 0f,
            BiomeBlend = blend,
            GentleAmplitude = 0f,
            Biomes = new[]
            {
                new BiomeBand { Start = float.NegativeInfinity, End = 0f, Biome = below, BaseHeight = 10f, HillAmplitude = 0f },
                new BiomeBand { Start = 0f, End = float.PositiveInfinity, Biome = above, BaseHeight = 10f, HillAmplitude = 0f },
            },
        });

        [Theory]
        [InlineData(BiomeId.Meadow, BiomeId.Desert)]
        [InlineData(BiomeId.Desert, BiomeId.Snow)]
        [InlineData(BiomeId.Forest, BiomeId.Mountains)]
        public void The_default_mix_fades_across_a_band_boundary_instead_of_switching(BiomeId below, BiomeId above)
        {
            // Flat open ground at one height, so only the biome changes along Z. The blended default moves by a small
            // amount per step and lands exactly on each biome's own mix outside the window. The discrete per-vertex
            // alternative jumps by the full difference between the two tilts in one step: that is the seam a single
            // triangle row would have drawn.
            TerrainField field = FlatBoundary(below, above, blend: 12f);
            const float step = 0.25f;
            TerrainSplatWeights Blended(float z) => TerrainSplatWeights.FromBlend(10f, 0f, field.SampleBiomeWeights(0f, z), 0f);
            TerrainSplatWeights Discrete(float z) => TerrainSplatWeights.From(10f, 0f, field.SampleBiome(0f, z), 0f);

            AssertBitIdentical(TerrainSplatWeights.From(10f, 0f, below, 0f), Blended(-30f), "deep in the lower band");
            AssertBitIdentical(TerrainSplatWeights.From(10f, 0f, above, 0f), Blended(30f), "deep in the upper band");

            float worstBlended = 0f, worstDiscrete = 0f;
            for (float z = -30f + step; z <= 30f; z += step)
            {
                worstBlended = MathF.Max(worstBlended, MaxChannelDelta(Blended(z - step), Blended(z)));
                worstDiscrete = MathF.Max(worstDiscrete, MaxChannelDelta(Discrete(z - step), Discrete(z)));
            }
            Assert.True(worstBlended < 0.02f, $"{below}->{above}: the blended mix jumped by {worstBlended} in one {step} m step");
            Assert.True(worstDiscrete > 0.25f, $"{below}->{above}: the discrete mix only jumped {worstDiscrete}, so this boundary shows nothing");
        }

        static float MaxChannelDelta(in TerrainSplatWeights a, in TerrainSplatWeights b) =>
            MathF.Max(MathF.Abs(a.Grass - b.Grass), MathF.Max(MathF.Abs(a.Dirt - b.Dirt),
            MathF.Max(MathF.Abs(a.Rock - b.Rock), MathF.Max(MathF.Abs(a.Sand - b.Sand), MathF.Abs(a.Snow - b.Snow)))));

        [Fact]
        public void The_builder_bakes_the_blended_default_across_a_boundary_chunk()
        {
            // A chunk straddling a Meadow to Desert boundary: every surface vertex carries FromBlend over the field's
            // shares, and neighbouring rows along Z differ by a small amount rather than one row jumping to sand.
            TerrainField field = FlatBoundary(BiomeId.Meadow, BiomeId.Desert, blend: 12f);
            var region = new TerrainChunkRegion { OriginX = -16f, OriginZ = -24f, Size = 48f };
            TerrainChunkMesh chunk = TerrainChunkBuilder.Build(field, region, lod: 0);
            int cols = TerrainLodConfig.Default.ResolutionFor(0) + 1;
            for (int i = 0; i < chunk.SurfaceVertexCount; i++)
            {
                Vector3 p = chunk.Mesh.Vertices[i].Position;
                float x = p.X + region.OriginX, z = p.Z + region.OriginZ;
                var expected = TerrainSplatWeights.FromBlend(field.SampleHeight(x, z), 1f - field.SampleNormal(x, z).Y,
                    field.SampleBiomeWeights(x, z), field.WaterLevel, 60f);
                AssertBitIdentical(expected, chunk.Splat[i], $"vertex {i}");
                if (i >= cols)
                {
                    float delta = MaxChannelDelta(chunk.Splat[i - cols], chunk.Splat[i]);
                    Assert.True(delta < 0.05f, $"vertex {i}: {delta} jump from the row before");
                }
            }
            Assert.True(chunk.Splat[0].Sand < 0.01f, "the Meadow edge of the chunk should carry no desert tilt");
            Assert.True(chunk.Splat[chunk.SurfaceVertexCount - 1].Sand > 0.75f, "the Desert edge should be sand");
        }

        [Fact]
        public void A_rule_that_ignores_Default_is_unaffected_by_the_tilt()
        {
            // The tilt only reaches ctx.Default. A consumer rule that builds its own mix bakes exactly that, even on a
            // multi-biome field, and the dominant biome it reads is still SampleBiome.
            TerrainField field = FlatBoundary(BiomeId.Snow, BiomeId.Marsh, blend: 12f);
            var region = new TerrainChunkRegion { OriginX = -16f, OriginZ = -24f, Size = 48f };
            TerrainChunkMesh chunk = TerrainChunkBuilder.Build(field, region, lod: 1,
                splatRule: ctx =>
                {
                    Assert.Equal(field.SampleBiome(ctx.WorldX, ctx.WorldZ), ctx.Biome);
                    return LegacyFrom(ctx.Height, ctx.Slope01, field.WaterLevel, 60f);
                });
            for (int i = 0; i < chunk.SurfaceVertexCount; i++)
            {
                Vector3 p = chunk.Mesh.Vertices[i].Position;
                float x = p.X + region.OriginX, z = p.Z + region.OriginZ;
                AssertBitIdentical(LegacyFrom(field.SampleHeight(x, z), 1f - field.SampleNormal(x, z).Y, field.WaterLevel, 60f),
                    chunk.Splat[i], $"vertex {i}");
            }
        }
    }
}
