using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// GPU coverage of the L3 HLOD cluster swap driven through the REAL <see cref="Scene3DChunkSink.Draw"/> wire
    /// (issue #276). Pixel-presence + <see cref="RenderFrameStats"/> evidence, NOT a golden: one chunk's forest is
    /// rendered twice, once with the focus AT the chunk (near = the individual props, many instances) and once with
    /// the focus far away (far = the single merged HLOD mesh, one instance). This proves the whole runtime wire -
    /// per-cluster distance to crossfade to the merged coarse mesh on screen. The crossfade curve and the
    /// dissolveFloor are exercised headlessly in PropHlodTests / PropRendererTests; the merge determinism + cache in
    /// Scene3DChunkSinkTests. Non-golden, so no new bake and no cross-platform gate. Skipped unless KE_GPU_TESTS=1.
    /// </summary>
    public sealed class PropHlodClusterGpuTests
    {
        readonly ITestOutputHelper _out;
        public PropHlodClusterGpuTests(ITestOutputHelper o) => _out = o;

        const int W = 128, H = 128;
        const float Size = 60f;

        // A flat meadow so a single-kind scatter reliably populates the chunk.
        static TerrainField FlatMeadow() => new TerrainField(new TerrainConfig
        {
            GentleAmplitude = 0f,
            WaterLevel = 0f,
            Biomes = new[]
            {
                new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, Biome = BiomeId.Meadow, BaseHeight = 2f, HillAmplitude = 0f },
            },
        });

        static ScatterConfig DenseMeadow(string id) => new ScatterConfig
        {
            Seed = 5,
            CellSize = 6f,
            Jitter = 0.4f,
            ClearingRadius = 0f,
            MaxHeight = null,
            Biomes = new[] { new BiomeScatterRule { Biome = BiomeId.Meadow, Density = 1f, Kinds = new[] { new PropKind(id, 1f) } } },
        };

        static int CoveredPixels(byte[] rgba)
        {
            int n = 0;
            for (int i = 0; i < rgba.Length; i += 4)
                if (rgba[i] > 40 || rgba[i + 1] > 40 || rgba[i + 2] > 40) n++;
            return n;
        }

        static RenderFrameStats RenderCluster(Vector3 focus, out int covered)
        {
            var field = FlatMeadow();
            ScatterConfig scatter = DenseMeadow("pine_a");
            GltfMesh kit = MeshPrimitives.Box(1.6f);
            Scene3DChunkSink sink = null!;
            RenderFrameStats stats = default;

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    scene.FrustumCulling = false;   // draw the whole cluster so the instance tally is deterministic
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.AmbientColor = new Color(0.7f, 0.7f, 0.7f, 1f);
                    scene.Post.BackgroundColor = new Color(0f, 0f, 0f, 1f);
                    var meshes = new Dictionary<string, MeshHandle> { ["pine_a"] = scene.LoadMesh(kit) };
                    var source = new Dictionary<string, GltfMesh> { ["pine_a"] = kit };
                    // HLOD 100 m out, hard swap (width 0): near focus draws props, far focus draws the merged mesh.
                    PropLayer layer = PropLayer.ScatterLayer(scatter, meshes, drawRadius: 800f)
                        .WithHlod(source, hlodDistance: 100f, weldCell: 2f);
                    sink = new Scene3DChunkSink(scene, field, new[] { layer }, chunkSize: Size);
                    sink.Load(new ChunkCoord(0, 0), lod: 0);
                    scene.Camera.Frame(new Vector3(30f, 4f, 30f), new Vector3(70f, 24f, 70f));
                },
                drawFrame: scene =>
                {
                    sink.Draw(focus);
                    stats = scene.LastFrameStats;
                },
                frames: 2);

            covered = CoveredPixels(rgba);
            return stats;
        }

        [GpuFact]
        public void Far_cluster_collapses_props_to_one_merged_instance_still_on_screen()
        {
            // Focus AT the chunk centre: every prop draws individually (crossfade t = 0).
            RenderFrameStats near = RenderCluster(new Vector3(30f, 4f, 30f), out int nearCovered);
            // Focus 1 km away: the whole cluster is past the HLOD distance, so it draws as one merged mesh (t = 1).
            RenderFrameStats far = RenderCluster(new Vector3(1030f, 4f, 30f), out int farCovered);

            _out.WriteLine($"near: draws={near.DrawCalls} instances={near.Instances} tris={near.Triangles} covered={nearCovered} | " +
                           $"far: draws={far.DrawCalls} instances={far.Instances} tris={far.Triangles} covered={farCovered}");

            Assert.True(nearCovered > 0, "the near cluster (individual props) must render");
            Assert.True(farCovered > 0, "the far cluster (merged HLOD mesh) must still render - the far world stays visible");
            Assert.True(near.Instances > 20, $"a populated cluster should draw many prop instances near ({near.Instances})");
            // Collapsed: the whole cluster is ONE merged instance past the HLOD distance (plus the terrain chunk's own
            // instance, so far.Instances is a small constant, not the ~100 individual props).
            Assert.True(far.Instances * 5 < near.Instances,
                $"the HLOD swap must collapse the per-prop instances (near {near.Instances}) to a handful (far {far.Instances})");
        }
    }
}
