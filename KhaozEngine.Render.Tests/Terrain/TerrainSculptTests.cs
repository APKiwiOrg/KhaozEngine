using System;
using System.Numerics;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>Composition of the authored sculpt-delta layer inside <see cref="TerrainField"/>: the empty
    /// fast-path identity, exact bilinear delta sampling, and normals recomputed from the composited surface.</summary>
    public class TerrainSculptTests
    {
        const int Size = TerrainSculpt.TileSize;

        static float[] ZeroTile() => new float[Size * Size];

        // A perfectly flat analytic base (no noise, no hills), so a sculpt delta is the ONLY height variation
        // and the composited surface is hand-computable.
        static TerrainConfig FlatConfig(float baseHeight) => new()
        {
            Seed = 3,
            GentleAmplitude = 0f,
            Biomes = new[]
            {
                new BiomeBand
                {
                    Start = float.NegativeInfinity, End = float.PositiveInfinity,
                    Biome = BiomeId.Meadow, BaseHeight = baseHeight, HillAmplitude = 0f,
                },
            },
        };

        [Fact]
        public void Empty_sculpt_is_byte_identical_to_the_analytic_field()
        {
            TerrainConfig cfg = TerrainPresets.Clearing();
            var analytic = new TerrainField(cfg);
            var withNull = new TerrainField(cfg, null);
            var withEmpty = new TerrainField(cfg, new TerrainSculpt(0.5f, Array.Empty<TerrainSculptTile>()));

            for (float x = -40f; x <= 40f; x += 7.5f)
            for (float z = -40f; z <= 40f; z += 7.5f)
            {
                float h = analytic.SampleHeight(x, z);
                Assert.Equal(h, withNull.SampleHeight(x, z));
                Assert.Equal(h, withEmpty.SampleHeight(x, z));

                Vector3 n = analytic.SampleNormal(x, z);
                Assert.Equal(n, withNull.SampleNormal(x, z));
                Assert.Equal(n, withEmpty.SampleNormal(x, z));
            }
        }

        [Fact]
        public void Single_tile_bilinear_is_exact_at_cell_centers_and_midpoints()
        {
            // Tile (0,0): global cell == local cell. Author four cells of a 2x2 block; the rest are 0.
            float[] deltas = ZeroTile();
            deltas[3 * Size + 2] = 5f;    // cell (2,3)
            deltas[3 * Size + 3] = 1f;    // cell (3,3)
            deltas[4 * Size + 2] = -3f;   // cell (2,4)
            // cell (3,4) stays 0
            const float cell = 0.5f;
            var sculpt = new TerrainSculpt(cell, new[] { new TerrainSculptTile(0, 0, deltas) });

            // Cell centers: exact authored value.
            Assert.Equal(5f, sculpt.SampleDelta(2 * cell, 3 * cell), 4);
            Assert.Equal(1f, sculpt.SampleDelta(3 * cell, 3 * cell), 4);
            Assert.Equal(-3f, sculpt.SampleDelta(2 * cell, 4 * cell), 4);
            Assert.Equal(0f, sculpt.SampleDelta(3 * cell, 4 * cell), 4);

            // Edge midpoints: the average of the two flanking cells.
            Assert.Equal(3f, sculpt.SampleDelta(2.5f * cell, 3f * cell), 4);   // between (2,3)=5 and (3,3)=1
            Assert.Equal(1f, sculpt.SampleDelta(2f * cell, 3.5f * cell), 4);   // between (2,3)=5 and (2,4)=-3

            // Square center: the mean of all four corners (5 + 1 - 3 + 0) / 4.
            Assert.Equal(0.75f, sculpt.SampleDelta(2.5f * cell, 3.5f * cell), 4);

            // Outside every stored tile: zero.
            Assert.Equal(0f, sculpt.SampleDelta(1000f, 1000f), 4);
        }

        [Fact]
        public void Sculpted_ramp_normal_tilts_and_matches_a_hand_computed_central_difference()
        {
            // A ramp linear in world X: delta(i,j) = a * i with a = cell, so d(delta)/dx = a / cell = 1.
            // The bilinear interpolation of a linear grid is exact, so the composited surface has slope 1 in
            // X and 0 in Z everywhere inside the tile.
            const float cell = 0.5f;
            float[] deltas = ZeroTile();
            for (int j = 0; j < Size; j++)
            for (int i = 0; i < Size; i++)
                deltas[j * Size + i] = cell * i;
            var sculpt = new TerrainSculpt(cell, new[] { new TerrainSculptTile(0, 0, deltas) });
            var field = new TerrainField(FlatConfig(10f), sculpt);

            // Well inside the tile (cell 16,16) so the +/- cell samples stay on the linear ramp.
            const float x = 16 * cell, z = 16 * cell;
            Vector3 n = field.SampleNormal(x, z);

            // The ramp climbs toward +X, so the normal leans toward -X, stays level in Z, and is tilted off +Y.
            Assert.True(n.X < 0f, $"expected the normal to lean -X, got X={n.X}");
            Assert.Equal(0f, n.Z, 4);
            Assert.True(n.Y < 0.999f, $"expected a tilted normal, got Y={n.Y}");
            // Slope 1 in X gives normalize(-1, 1, 0) = (-0.7071, 0.7071, 0).
            Assert.Equal(-0.70710678f, n.X, 4);
            Assert.Equal(0.70710678f, n.Y, 4);

            // SampleNormal must be the central difference of the COMPOSITED height at the sculpt cell size.
            float eps = cell;
            float hxp = field.SampleHeight(x + eps, z), hxm = field.SampleHeight(x - eps, z);
            float hzp = field.SampleHeight(x, z + eps), hzm = field.SampleHeight(x, z - eps);
            Vector3 expected = Vector3.Normalize(new Vector3(-(hxp - hxm) / (2f * eps), 1f, -(hzp - hzm) / (2f * eps)));
            Assert.Equal(expected, n);
        }

        [Fact]
        public void SampleHeight_adds_the_delta_only_where_a_tile_covers_the_point()
        {
            const float cell = 0.5f;
            TerrainConfig cfg = TerrainPresets.Clearing();
            var plain = new TerrainField(cfg);

            float[] deltas = ZeroTile();
            deltas[10 * Size + 10] = 7f;   // cell (10,10)
            var sculpt = new TerrainSculpt(cell, new[] { new TerrainSculptTile(0, 0, deltas) });
            var sculpted = new TerrainField(cfg, sculpt);

            // At the sculpted cell center the delta is exact.
            Assert.Equal(plain.SampleHeight(10 * cell, 10 * cell) + 7f, sculpted.SampleHeight(10 * cell, 10 * cell), 4);
            // Far from any tile the sculpted field equals the analytic field.
            Assert.Equal(plain.SampleHeight(500f, 500f), sculpted.SampleHeight(500f, 500f));
        }

        [Fact]
        public void Negative_cells_map_to_the_expected_tile_and_local_cell()
        {
            const float cell = 0.5f;
            // Cell (-1, -1) lives in tile (-1, -1) at local (31, 31).
            float[] deltas = ZeroTile();
            deltas[31 * Size + 31] = 4f;
            var sculpt = new TerrainSculpt(cell, new[] { new TerrainSculptTile(-1, -1, deltas) });
            Assert.Equal(4f, sculpt.SampleDelta(-1 * cell, -1 * cell), 4);
        }

        [Fact]
        public void Bad_inputs_throw()
        {
            Assert.Throws<ArgumentException>(() => new TerrainSculptTile(0, 0, new float[10]));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TerrainSculpt(0f, Array.Empty<TerrainSculptTile>()));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TerrainSculpt(float.PositiveInfinity, Array.Empty<TerrainSculptTile>()));
        }
    }
}
