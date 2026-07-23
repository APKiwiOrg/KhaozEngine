using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Terrain
{
    // Visual parity for a FAR LOD tier: a chunk meshed at the default config's coarsest tiers (8- then 4-segment)
    // must still render as lit, textured ground - not vanish, not blow out to white. Named "...Golden..." so the
    // cross-platform-gpu matrix sweeps it on each backend (Metal / D3D11-WARP / Vulkan-lavapipe) via
    // `--filter FullyQualifiedName~Golden`. Assertion-based (no committed per-backend reference grid), so it needs
    // no bake and is portable across backends, mirroring the robustness of SplatTerrainGoldenTests.
    public sealed class TerrainFarTierGoldenTests
    {
        readonly ITestOutputHelper _out;
        public TerrainFarTierGoldenTests(ITestOutputHelper o) => _out = o;

        [GpuFact]
        public void FarTierTerrainGoldenIsTexturedNotWhite()
        {
            const int W = 96, H = 96;
            var field = new TerrainField(TerrainPresets.Clearing());

            // Mesh a 32 m chunk at the coarsest default tier (4 segments). Sanity-pin that we are actually exercising a
            // far tier, not the legacy near ones. Same chunk size + camera framing as SplatTerrainGoldenTests (a
            // proven-good top-down frame), so only the mesh DENSITY differs from that near-tier golden.
            int farLod = TerrainLodConfig.Default.TierCount - 1;
            Assert.Equal(4, TerrainLodConfig.Default.ResolutionFor(farLod));
            var chunk = TerrainChunkBuilder.Build(field, new TerrainChunkRegion { OriginX = 0f, OriginZ = 0f, Size = 32f }, lod: farLod);
            Assert.Equal((4 + 1) * (4 + 1), chunk.SurfaceVertexCount);   // a 4-segment grid, not a 64-segment one

            MeshHandle h = default;
            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    var mat = scene.LoadTerrainMaterial(TerrainMaterialPresets.Procedural(32));
                    h = scene.LoadTerrainChunk(chunk, mat);
                    scene.Camera.Frame(new Vector3(16f, 1f, 16f), new Vector3(16f, 26f, 16.4f));
                },
                drawFrame: scene => scene.DrawTerrainChunk(h));

            int Idx(int x, int y) => (y * W + x) * 4;
            (byte r, byte g, byte b) At(int x, int y) { int i = Idx(x, y); return (rgba[i], rgba[i + 1], rgba[i + 2]); }

            var grid = new StringBuilder("far-tier grid (r,g,b): ");
            int nearWhite = 0, lit = 0, samples = 0;
            for (int gy = 0; gy < 5; gy++)
                for (int gx = 0; gx < 5; gx++)
                {
                    int px = W / 4 + gx * (W / 2) / 4;
                    int py = H / 4 + gy * (H / 2) / 4;
                    var (r, g, b) = At(px, py);
                    grid.Append($"{r},{g},{b}|");
                    samples++;
                    bool background = r < 30 && g < 30 && b < 30;
                    if (!background) lit++;
                    if (r >= 235 && g >= 235 && b >= 235) nearWhite++;
                }

            var (cr, cg, cb) = At(W / 2, H / 2);
            int spread = Math.Max(cr, Math.Max(cg, cb)) - Math.Min(cr, Math.Min(cg, cb));
            string msg = $"centre=({cr},{cg},{cb}) spread={spread} nearWhite={nearWhite}/{samples} lit={lit}/{samples}. {grid}";
            _out.WriteLine(msg);

            // Same backend-robust gates as the near splat golden: the coarse far terrain centre is a tinted texture,
            // lit, and not the D3D11 white-out. A far tier changes only the mesh density, not the shading path, so
            // these hold on every backend exactly as they do for the dense golden.
            Assert.True(lit >= 12, $"the far-tier chunk barely rendered ({lit}/{samples} lit): framing or load wrong. {msg}");
            Assert.True(nearWhite == 0, $"far-tier terrain is near-white (splat material not rendered). {msg}");
            Assert.True(spread >= 15, $"far-tier terrain centre is flat grey/white, not a tinted texture. {msg}");
        }
    }

    // RenderFrameStats regression evidence for the far field: drawing the SAME number of chunks out to the horizon
    // must cost far FEWER triangles when distant chunks use coarse tiers than when every chunk is meshed dense. Reads
    // the always-on Scene3D.LastFrameStats (the frames:2 capture sees the prior frame's finalized totals). GPU-gated
    // because the counters are finalized inside the render pass.
    public sealed class TerrainFarFieldFrameStatsTests
    {
        readonly ITestOutputHelper _out;
        public TerrainFarFieldFrameStatsTests(ITestOutputHelper o) => _out = o;

        const int W = 160, H = 120;
        const int Strip = 21;                                  // chunks receding along +Z, z origins 0..1200 m
        const float Size = TerrainChunkRegion.DefaultSize;     // 60 m

        static RenderFrameStats RenderStrip(bool farField)
        {
            var field = new TerrainField(TerrainPresets.Clearing());
            var handles = new List<MeshHandle>();
            var captured = default(RenderFrameStats);
            Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    scene.FrustumCulling = false;   // draw every chunk so the triangle tally is deterministic
                    scene.Post.Starfield = false;
                    var mat = scene.LoadTerrainMaterial(TerrainMaterialPresets.Procedural(32));
                    for (int cz = 0; cz < Strip; cz++)
                    {
                        var region = new TerrainChunkRegion { OriginX = 0f, OriginZ = cz * Size, Size = Size };
                        // Distance from the origin camera to the chunk centre (x = 30 m, z = cz*60 + 30 m).
                        float dist = MathF.Sqrt(30f * 30f + (cz * Size + 30f) * (cz * Size + 30f));
                        int lod = farField ? TerrainLodConfig.Default.PickLod(dist) : 0;   // near-only meshes all dense
                        handles.Add(scene.LoadTerrainChunk(TerrainChunkBuilder.Build(field, region, lod), mat));
                    }
                    scene.Camera.Frame(new Vector3(30f, 2f, 0f), new Vector3(30f, 2f, 60f));
                },
                drawFrame: scene =>
                {
                    foreach (MeshHandle h in handles) scene.DrawTerrainChunk(h);
                    captured = scene.LastFrameStats;
                },
                frames: 2);
            return captured;
        }

        [GpuFact]
        public void Far_field_costs_far_fewer_triangles_than_meshing_every_chunk_dense()
        {
            RenderFrameStats nearOnly = RenderStrip(farField: false);
            RenderFrameStats farField = RenderStrip(farField: true);

            _out.WriteLine($"near-only: draws={nearOnly.DrawCalls} tris={nearOnly.Triangles} | " +
                           $"far-field: draws={farField.DrawCalls} tris={farField.Triangles} | " +
                           $"ratio={(double)farField.Triangles / nearOnly.Triangles:F3}");

            Assert.True(nearOnly.Triangles > 0 && farField.Triangles > 0, "both configs must draw terrain");
            // Same coverage: the far field draws the same chunk count (same horizon), so the SAVING is all in the
            // triangle budget, not in hiding geometry.
            Assert.True(farField.DrawCalls >= Strip, $"the far field should still draw every chunk ({farField.DrawCalls} draws)");
            // Sub-proportional: distant chunks use coarse tiers, so the far field is a fraction of the dense cost.
            Assert.True(farField.Triangles < nearOnly.Triangles, "the far field must cost fewer triangles than all-dense");
            Assert.True(farField.Triangles * 2 < nearOnly.Triangles,
                $"the far field ({farField.Triangles} tris) should be well under half the dense cost ({nearOnly.Triangles} tris)");
        }
    }
}
