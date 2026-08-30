using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    // Tier-aware skirt depth (issue #100). Two halves: the arithmetic of TerrainLodConfig.SkirtDepthFor, and the
    // geometry it exists for - the slit where a fine chunk meets a coarse one, measured off the built meshes rather
    // than off a picture, so it needs no GPU and no golden. Headless: TerrainChunkBuilder.Build is CPU only.
    public class TerrainSkirtDepthTests
    {
        const float Chunk = 60f;
        static TerrainLodConfig Cfg => TerrainLodConfig.Default;

        [Fact]
        public void Default_table_scales_the_depth_with_the_coarsest_neighbouring_cell()
        {
            // 60 m chunks, tiers 64/32/16/8/4. Each tier's skirt is half the cell of the coarsest tier that can
            // meet its edge, which on this table is the next one out (and itself for the last).
            Assert.Equal(0.9375f, Cfg.SkirtDepthFor(0, Chunk));   // half of 60/32
            Assert.Equal(1.875f, Cfg.SkirtDepthFor(1, Chunk));    // half of 60/16
            Assert.Equal(3.75f, Cfg.SkirtDepthFor(2, Chunk));     // half of 60/8
            Assert.Equal(7.5f, Cfg.SkirtDepthFor(3, Chunk));      // half of 60/4
            Assert.Equal(7.5f, Cfg.SkirtDepthFor(4, Chunk));      // coarsest tier: no coarser neighbour to cover
        }

        [Fact]
        public void Depth_is_proportional_to_the_cell_world_size()
        {
            // Wider chunks, same tiers: the cell grows with the chunk and so does every tier's skirt. Measured on a
            // table whose thresholds are far enough apart that the neighbour a chunk can reach is the next tier at
            // both sizes, which is what isolates the proportionality from the lookup.
            var spread = new TerrainLodConfig(
                new TerrainLodTier(64, 400f),
                new TerrainLodTier(32, 800f),
                new TerrainLodTier(16, 1600f),
                new TerrainLodTier(8, float.PositiveInfinity));
            for (int lod = 0; lod < spread.TierCount; lod++)
                Assert.Equal(4f * spread.SkirtDepthFor(lod, Chunk), spread.SkirtDepthFor(lod, Chunk * 4f), 4);

            // On Default the same widening buys MORE than 4x at the near tiers, because a chunk four times as wide
            // reaches four times as far past its own tier boundary and so can meet a coarser neighbour than before.
            Assert.True(Cfg.SkirtDepthFor(0, Chunk * 4f) > 4f * Cfg.SkirtDepthFor(0, Chunk));
        }

        [Fact]
        public void The_flat_legacy_depth_is_a_floor_not_a_default()
        {
            // A chunk small enough (or a table dense enough) that half a cell is under the legacy 0.3 m still gets
            // 0.3 m: the floor only ever raises a depth, so nothing is meshed thinner than the near ring used to be.
            Assert.Equal(TerrainLodConfig.MinSkirtDepth, Cfg.SkirtDepthFor(0, chunkSize: 4f));
            Assert.True(Cfg.SkirtDepthFor(0, chunkSize: 4f) > 0.5f * (4f / 32f));
            // And it does not cap: the coarse tiers of the same small chunk are still free to exceed it.
            Assert.True(Cfg.SkirtDepthFor(4, chunkSize: 40f) > TerrainLodConfig.MinSkirtDepth);
        }

        [Fact]
        public void A_custom_fraction_overrides_the_default_one()
        {
            Assert.Equal(2f * 0.9375f, Cfg.SkirtDepthFor(0, Chunk, cellFraction: 2f * TerrainLodConfig.SkirtCellFraction));
            // Still floored: a fraction of nearly nothing does not mesh a nearly-invisible skirt.
            Assert.Equal(TerrainLodConfig.MinSkirtDepth, Cfg.SkirtDepthFor(0, Chunk, cellFraction: 0.0001f));
        }

        [Fact]
        public void Lod_index_clamps_like_ResolutionFor()
        {
            Assert.Equal(Cfg.SkirtDepthFor(0, Chunk), Cfg.SkirtDepthFor(-3, Chunk));
            Assert.Equal(Cfg.SkirtDepthFor(Cfg.TierCount - 1, Chunk), Cfg.SkirtDepthFor(Cfg.TierCount + 7, Chunk));
        }

        [Fact]
        public void A_table_that_can_skip_a_tier_at_a_seam_gets_the_deeper_skirt()
        {
            // Tiers are picked from the distance to a chunk's CENTER and edge-sharing centres are one chunk apart, so
            // a table whose thresholds are packed closer than a chunk is wide can put a tier-0 chunk next to a tier-2
            // one. The depth follows the table rather than assuming the neighbour is one tier out.
            var packed = new TerrainLodConfig(
                new TerrainLodTier(64, 100f),
                new TerrainLodTier(32, 130f),
                new TerrainLodTier(16, 400f),
                new TerrainLodTier(8, float.PositiveInfinity));
            // 100 + 60 (chunk) + 10 (hysteresis) = 170, which is tier 2 on this table: half of 60/16, not of 60/32.
            Assert.Equal(0.5f * (Chunk / 16f), packed.SkirtDepthFor(0, Chunk));

            var spread = new TerrainLodConfig(
                new TerrainLodTier(64, 400f),
                new TerrainLodTier(32, 800f),
                new TerrainLodTier(16, 1600f),
                new TerrainLodTier(8, float.PositiveInfinity));
            Assert.Equal(0.5f * (Chunk / 32f), spread.SkirtDepthFor(0, Chunk));
        }

        [Fact]
        public void Skirts_close_the_seam_between_every_pair_of_default_tiers()
        {
            // The property the pictures show, as geometry. Two chunks share an edge and sample it at different
            // spacings, so between the coarse side's samples the two surfaces separate by up to half a coarse cell of
            // daylight. Each side's skirt has to reach at least as far down as the other side's surface, everywhere
            // along that edge, or the gap is see-through. Read off the built meshes: no device, no golden.
            var field = new TerrainField(TerrainPresets.Clearing());   // the mountain band, where a 22 m hill amplitude bites
            for (int lod = 0; lod < Cfg.TierCount - 1; lod++)
            {
                float fineDepth = Cfg.SkirtDepthFor(lod, Chunk), coarseDepth = Cfg.SkirtDepthFor(lod + 1, Chunk);
                float worst = 0f;
                for (int cx = 0; cx < 4; cx++)
                {
                    TerrainChunkMesh fine = TerrainChunkBuilder.Build(
                        field, new TerrainChunkRegion { OriginX = cx * Chunk, OriginZ = 300f, Size = Chunk }, lod, Cfg, fineDepth);
                    TerrainChunkMesh coarse = TerrainChunkBuilder.Build(
                        field, new TerrainChunkRegion { OriginX = (cx + 1) * Chunk, OriginZ = 300f, Size = Chunk }, lod + 1, Cfg, coarseDepth);

                    float[] fineTop = Edge(fine, Cfg.ResolutionFor(lod), maxX: true, skirt: false);
                    float[] fineBottom = Edge(fine, Cfg.ResolutionFor(lod), maxX: true, skirt: true);
                    float[] coarseTop = Edge(coarse, Cfg.ResolutionFor(lod + 1), maxX: false, skirt: false);
                    float[] coarseBottom = Edge(coarse, Cfg.ResolutionFor(lod + 1), maxX: false, skirt: true);

                    // Sample far denser than either edge: the worst separation sits between the coarse samples.
                    for (int s = 0; s <= 512; s++)
                    {
                        float t = s / 512f;
                        float fineY = At(fineTop, t), coarseY = At(coarseTop, t);
                        Assert.True(At(fineBottom, t) <= coarseY + 1e-3f,
                            $"tier {lod} skirt stops {At(fineBottom, t) - coarseY:F3} m above the tier {lod + 1} surface at t={t:F3}");
                        Assert.True(At(coarseBottom, t) <= fineY + 1e-3f,
                            $"tier {lod + 1} skirt stops {At(coarseBottom, t) - fineY:F3} m above the tier {lod} surface at t={t:F3}");
                        worst = MathF.Max(worst, MathF.Abs(fineY - coarseY));
                    }
                }
                // And the seam really does defeat the flat depth this replaced, at EVERY tier pair including the
                // densest, or the assertions above would pass on a 0.3 m skirt too and pin nothing.
                Assert.True(worst > TerrainLodConfig.MinSkirtDepth,
                    $"tier {lod}/{lod + 1} seam only opens {worst:F3} m, so it does not exercise the depth at all");
            }
        }

        [Fact]
        public void Sink_meshes_every_chunk_with_its_tier_s_depth()
        {
            // The seam a game actually configures: games drive the streamer through the sink, they do not call
            // TerrainChunkBuilder.Build, so this is where the tier-aware depth has to land.
            var field = new TerrainField(TerrainPresets.Clearing());
            var sink = new Scene3DChunkSink(scene: null!, field, new ScatterConfig(),
                propMeshes: new Dictionary<string, MeshHandle>(), chunkSize: Chunk, propDrawRadius: 90f);

            for (int lod = 0; lod < Cfg.TierCount; lod++)
            {
                var cpu = (Scene3DChunkSink.CpuBuild)sink.BuildCpu(new ChunkCoord(2, 5), lod);
                // The first skirt vertex is the dropped copy of surface vertex 0, so their height difference IS the
                // depth the chunk was meshed with.
                ModelVertex[] v = cpu.Mesh.Mesh.Vertices;
                float depth = v[0].Position.Y - v[cpu.Mesh.SurfaceVertexCount].Position.Y;
                Assert.Equal(Cfg.SkirtDepthFor(lod, Chunk), depth, 4);
                Assert.True(depth > TerrainLodConfig.MinSkirtDepth, $"tier {lod} still meshed the flat legacy depth");
            }
        }

        // --- edge readers ------------------------------------------------------------------------------------------
        // A chunk's -X / +X boundary column, either the surface row or the skirt row hanging under it. Skirts are
        // appended after the surface grid in -Z, +Z, -X, +X order, one dropped copy of each edge vertex.
        static float[] Edge(TerrainChunkMesh m, int res, bool maxX, bool skirt)
        {
            int cols = res + 1;
            ModelVertex[] v = m.Mesh.Vertices;
            var ys = new float[cols];
            for (int iz = 0; iz <= res; iz++)
                ys[iz] = skirt
                    ? v[m.SurfaceVertexCount + (maxX ? 3 : 2) * cols + iz].Position.Y
                    : v[iz * cols + (maxX ? res : 0)].Position.Y;
            return ys;
        }

        // What the mesh actually renders between two samples of an edge is the straight chord, so a point between
        // them reads as the linear blend of its ends.
        static float At(float[] ys, float t01)
        {
            int n = ys.Length - 1;
            float f = Math.Clamp(t01, 0f, 1f) * n;
            int i = Math.Clamp((int)MathF.Floor(f), 0, n - 1);
            float frac = f - i;
            return ys[i] * (1f - frac) + ys[i + 1] * frac;
        }
    }
}
