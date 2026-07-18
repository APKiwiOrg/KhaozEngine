using System;
using System.Numerics;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    public class RimFeatureTests
    {
        static RimFeature Ring(float ruggedness = 0f, RimPass[]? passes = null) =>
            new RimFeature(Vector2.Zero, innerRadius: 40f, outerRadius: 60f, wallHeight: 30f,
                ruggedness: ruggedness, passes: passes, seed: 7);

        [Fact]
        public void Inside_inner_radius_is_unchanged()
        {
            var rim = Ring();
            Assert.Equal(5f, rim.Apply(0f, 0f, 5f), 4);
            Assert.Equal(5f, rim.Apply(30f, 0f, 5f), 4);   // still inside inner=40
        }

        [Fact]
        public void Ramps_to_wall_height_by_outer_radius_smooth_crest()
        {
            var rim = Ring();                               // ruggedness 0 -> exact
            float onWall = rim.Apply(60f, 0f, 0f);         // at outer radius
            float beyond = rim.Apply(80f, 0f, 0f);         // past outer -> plateau, still WallHeight
            Assert.Equal(30f, onWall, 2);
            Assert.Equal(30f, beyond, 2);
            float mid = rim.Apply(50f, 0f, 0f);            // halfway up the ramp
            Assert.True(mid > 5f && mid < 30f);
        }

        [Fact]
        public void Jagged_crest_stays_within_band_and_varies_by_position()
        {
            var rim = Ring(ruggedness: 0.3f);
            float a = rim.Apply(60f, 0f, 0f);
            float b = rim.Apply(0f, 60f, 0f);
            Assert.InRange(a, 30f * 0.7f - 0.01f, 30f * 1.3f + 0.01f);
            Assert.InRange(b, 30f * 0.7f - 0.01f, 30f * 1.3f + 0.01f);
            Assert.NotEqual(a, b, 3);                       // crest is jagged, not a uniform berm
        }

        [Fact]
        public void Pass_corridor_stays_low()
        {
            // one pass heading +X (angle 0): the +X wall is cut open.
            var rim = Ring(passes: new[] { new RimPass(angleRadians: 0f, halfWidth: 8f, falloff: 6f) });
            float atPass = rim.Apply(60f, 0f, 0f);         // along +X at outer radius -> open
            float offPass = rim.Apply(0f, 60f, 0f);        // +Z wall, far from the pass -> full wall
            Assert.True(atPass < 3f, $"pass not open: {atPass}");
            Assert.True(offPass > 25f, $"wall too low: {offPass}");
        }

        [Fact]
        public void Pass_only_opens_along_its_heading_not_the_opposite_wall()
        {
            var rim = Ring(passes: new[] { new RimPass(angleRadians: 0f, halfWidth: 8f, falloff: 6f) });
            float opposite = rim.Apply(-60f, 0f, 0f);      // -X wall: behind the heading -> still a wall
            Assert.True(opposite > 25f, $"opposite wall opened: {opposite}");
        }

        [Fact]
        public void Deterministic_in_position_and_seed()
        {
            var rim = Ring(ruggedness: 0.3f);
            Assert.Equal(rim.Apply(55f, 7f, 0f), rim.Apply(55f, 7f, 0f), 6);   // pure in (x,z)
            var other = new RimFeature(Vector2.Zero, 40f, 60f, 30f, ruggedness: 0.3f, seed: 99);
            Assert.NotEqual(rim.Apply(55f, 7f, 0f), other.Apply(55f, 7f, 0f), 4);   // seed changes the crest
        }

        [Fact]
        public void Composes_with_lake_and_flatten_in_a_field()
        {
            var cfg = new TerrainConfig
            {
                Seed = 3,
                Biomes = new[] { new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, Biome = BiomeId.Meadow, BaseHeight = 0f, HillAmplitude = 0f } },
                GentleAmplitude = 0f,
                Features = new ITerrainFeature[]
                {
                    new LakeFeature(centerX: -12f, centerZ: 0f, radius: 8f, depth: 4f),
                    new FlattenFeature(centerX: 10f, centerZ: 0f, radius: 6f, targetHeight: 1f),
                    new RimFeature(Vector2.Zero, 40f, 60f, 30f, ruggedness: 0f, seed: 3),
                },
            };
            var field = new TerrainField(cfg);
            Assert.True(field.SampleHeight(-12f, 0f) < -1f, "lake not carved under the rim");
            Assert.Equal(1f, field.SampleHeight(10f, 0f), 1);            // flattened pad
            Assert.True(field.SampleHeight(0f, 60f) > 25f, "rim wall not raised in the field");
        }

        [Fact]
        public void Rim_wall_is_unwalkable_but_the_pass_is_walkable()
        {
            var cfg = new TerrainConfig
            {
                Seed = 3,
                Biomes = new[] { new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, Biome = BiomeId.Meadow, BaseHeight = 0f, HillAmplitude = 0f } },
                GentleAmplitude = 0f,
                Features = new ITerrainFeature[]
                {
                    new RimFeature(Vector2.Zero, 40f, 56f, 30f, ruggedness: 0f, seed: 3,
                        passes: new[] { new RimPass(angleRadians: 0f, halfWidth: 8f, falloff: 6f) }),
                },
            };
            var col = new TerrainCollision(new TerrainField(cfg));
            float maxSlope = MathF.PI * 50f / 180f;
            Assert.True(col.GroundNormal(0f, 0f).Y > 0.99f);                      // flat inside
            Assert.False(col.IsWalkable(0f, 48f, maxSlope), "rim wall mid-band should be too steep");
            Assert.True(col.IsWalkable(48f, 0f, maxSlope), "the pass corridor should stay walkable");
        }

        [Fact]
        public void BoundedClearing_is_flat_inside_and_walled_around()
        {
            var field = new TerrainField(TerrainPresets.BoundedClearing());
            float maxSlope = MathF.PI * 50f / 180f;
            var col = new TerrainCollision(field);
            Assert.True(MathF.Abs(field.SampleHeight(0f, 0f)) < 4f, "centre should be roughly flat meadow");
            Assert.True(field.SampleHeight(0f, -55f) > 20f, "south should be walled by the rim");
            Assert.True(field.SampleHeight(55f, 0f) > 20f, "east should be walled by the rim");
            Assert.True(field.SampleHeight(0f, 55f) < 10f, "the +Z pass should be open (the road out)");
            Assert.True(col.IsWalkable(0f, 0f, maxSlope), "the clearing floor should be walkable");
        }
    }
}
