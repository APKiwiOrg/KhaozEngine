using System;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    // Data-driven LOD tiers: config validation, the extended default distance table, and the byte-identical-mesh
    // guarantee for existing callers (the near three tiers must reproduce the pre-data-driven 64/32/16 meshes
    // exactly, so the far tiers are purely additive). Headless: TerrainChunkBuilder.Build is CPU only.
    public class TerrainLodConfigTests
    {
        static TerrainLodTier T(int res, float max) => new(res, max);
        static readonly TerrainChunkRegion Region = new() { OriginX = 0f, OriginZ = 0f, Size = 60f };
        static TerrainField Field() => new TerrainField(TerrainPresets.Clearing());

        [Fact]
        public void Ctor_rejects_an_empty_tier_list()
        {
            Assert.Throws<ArgumentException>(() => new TerrainLodConfig());
            Assert.Throws<ArgumentNullException>(() => new TerrainLodConfig(null!));
        }

        [Fact]
        public void Ctor_requires_strictly_descending_resolutions()
        {
            // Equal resolutions are rejected (must be strictly coarser with distance).
            Assert.Throws<ArgumentException>(() => new TerrainLodConfig(T(32, 80f), T(32, float.PositiveInfinity)));
            // Ascending resolution is rejected.
            Assert.Throws<ArgumentException>(() => new TerrainLodConfig(T(16, 80f), T(32, float.PositiveInfinity)));
        }

        [Fact]
        public void Ctor_requires_strictly_ascending_max_distances()
        {
            Assert.Throws<ArgumentException>(() => new TerrainLodConfig(T(32, 200f), T(16, 200f)));
            Assert.Throws<ArgumentException>(() => new TerrainLodConfig(T(32, 200f), T(16, 100f)));
        }

        [Fact]
        public void Ctor_requires_the_coarsest_tier_to_reach_infinity()
        {
            Assert.Throws<ArgumentException>(() => new TerrainLodConfig(T(32, 80f), T(16, 200f)));   // last is finite
        }

        [Fact]
        public void Tier_ctor_rejects_bad_resolution_and_distance()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TerrainLodTier(0, 80f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TerrainLodTier(16, 0f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TerrainLodTier(16, -5f));
        }

        [Fact]
        public void Single_tier_config_is_valid_and_covers_all_distances()
        {
            var one = new TerrainLodConfig(T(16, float.PositiveInfinity));
            Assert.Equal(1, one.TierCount);
            Assert.Equal(0, one.PickLod(0f));
            Assert.Equal(0, one.PickLod(9999f));
            Assert.Equal(16, one.ResolutionFor(0));
            Assert.Equal(16, one.ResolutionFor(5));   // clamped
        }

        [Fact]
        public void Default_reproduces_the_legacy_near_three_tiers()
        {
            TerrainLodConfig d = TerrainLodConfig.Default;
            Assert.Equal(64, d.ResolutionFor(0));
            Assert.Equal(32, d.ResolutionFor(1));
            Assert.Equal(16, d.ResolutionFor(2));
            // Legacy thresholds: 80 m and 200 m.
            Assert.Equal(0, d.PickLod(79f));
            Assert.Equal(1, d.PickLod(80f));
            Assert.Equal(1, d.PickLod(199f));
            Assert.Equal(2, d.PickLod(200f));
            Assert.Equal(2, d.PickLod(400f));   // still tier 2 (was the terminal tier's range before)
        }

        [Fact]
        public void Default_adds_coarser_far_tiers_beyond_the_legacy_three()
        {
            TerrainLodConfig d = TerrainLodConfig.Default;
            Assert.True(d.TierCount >= 5, $"expected the default to add far tiers, got {d.TierCount}");
            int tier700 = d.PickLod(700f);
            int tier1500 = d.PickLod(1500f);
            Assert.True(tier700 > 2, "a chunk at 700 m should reach a coarser-than-legacy tier");
            Assert.True(tier1500 > tier700, "a chunk at 1500 m should be coarser still");
            // Far tiers are cheap: 8 segments then 4 (a few hundred triangles vs the mid tier's few thousand).
            Assert.Equal(8, d.ResolutionFor(tier700));
            Assert.Equal(4, d.ResolutionFor(tier1500));
            // The coarsest tier catches arbitrarily far chunks.
            Assert.Equal(d.TierCount - 1, d.PickLod(50_000f));
        }

        [Fact]
        public void PickLod_is_monotone_non_decreasing_across_the_whole_default_table()
        {
            TerrainLodConfig d = TerrainLodConfig.Default;
            int prev = d.PickLod(0f);
            for (float dist = 0f; dist < 3000f; dist += 5f)
            {
                int lod = d.PickLod(dist);
                Assert.True(lod >= prev, $"PickLod dipped at {dist} m ({lod} < {prev})");
                prev = lod;
            }
        }

        // --- Byte-identical meshes for existing callers -----------------------------------------------------------

        static void AssertMeshBytesEqual(TerrainChunkMesh a, TerrainChunkMesh b)
        {
            Assert.Equal(a.Lod, b.Lod);
            Assert.Equal(a.SurfaceVertexCount, b.SurfaceVertexCount);

            ModelVertex[] va = a.Mesh.Vertices, vb = b.Mesh.Vertices;
            Assert.Equal(va.Length, vb.Length);
            for (int i = 0; i < va.Length; i++)
            {
                Assert.Equal(va[i].Position, vb[i].Position);
                Assert.Equal(va[i].Normal, vb[i].Normal);
                Assert.Equal(va[i].Color, vb[i].Color);
                Assert.Equal(va[i].Uv, vb[i].Uv);
            }

            uint[] ia = a.Mesh.Indices32, ib = b.Mesh.Indices32;
            Assert.Equal(ia.Length, ib.Length);
            for (int i = 0; i < ia.Length; i++) Assert.Equal(ia[i], ib[i]);

            Assert.Equal(a.Splat.Length, b.Splat.Length);
            for (int i = 0; i < a.Splat.Length; i++) Assert.Equal(a.Splat[i], b.Splat[i]);
        }

        [Fact]
        public void Default_config_meshes_are_byte_identical_to_the_legacy_three_tiers()
        {
            // The exact legacy table (64/32/16 at 80/200, terminal at 16). The default extends this with far tiers,
            // but for tiers 0/1/2 the resolution is unchanged, so the meshes must be vertex-for-vertex identical -
            // and identical again to the default (no-config) Build overload existing callers use.
            var legacy = new TerrainLodConfig(T(64, 80f), T(32, 200f), T(16, float.PositiveInfinity));
            TerrainField field = Field();
            for (int lod = 0; lod <= 2; lod++)
            {
                TerrainChunkMesh viaLegacy = TerrainChunkBuilder.Build(field, Region, lod, legacy);
                TerrainChunkMesh viaDefault = TerrainChunkBuilder.Build(field, Region, lod, TerrainLodConfig.Default);
                TerrainChunkMesh viaOverload = TerrainChunkBuilder.Build(field, Region, lod);   // the pre-data-driven signature
                AssertMeshBytesEqual(viaLegacy, viaDefault);
                AssertMeshBytesEqual(viaLegacy, viaOverload);
            }
        }

        [Fact]
        public void TerrainLod_facade_delegates_to_the_default_config()
        {
            Assert.Equal(TerrainLodConfig.Default.TierCount, TerrainLod.TierCount);
            Assert.Equal(TerrainLodConfig.Default.PickLod(150f), TerrainLod.PickLod(150f));
            Assert.Equal(TerrainLodConfig.Default.ResolutionFor(1), TerrainLod.ResolutionFor(1));
        }
    }
}
