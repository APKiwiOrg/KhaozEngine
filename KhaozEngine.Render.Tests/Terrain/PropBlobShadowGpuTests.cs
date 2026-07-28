using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>
    /// GPU coverage of the two halves of the blob-shadow prop seam (issue #388) that need a REAL
    /// <see cref="Scene3D"/> and so cannot run headlessly: the resolved-mode gate on
    /// <see cref="PropRenderer.DrawProps(Scene3D, IReadOnlyList{PropPlacement}, IReadOnlyDictionary{string, MeshHandle}, Vector3, float, Color?, float, IReadOnlyDictionary{string, MeshHandle}, float, float, bool, IReadOnlyDictionary{string, float})"/>
    /// (a layer's <see cref="PropLayer.BlobRadii"/> table only fires while the scene's resolved shadow tier is
    /// <see cref="ShadowMode.Blob"/>), and that <see cref="Scene3DChunkSink"/>'s merged-HLOD branch never
    /// registers a blob (no per-placement data there, so a layer's blobs stop at the HLOD swap automatically).
    /// The per-kit lookup, scale multiplication, and full-dissolve skip are exercised headlessly in
    /// PropRendererBlobTests; this file only proves the two GPU-only wires. Skipped unless KE_GPU_TESTS is set
    /// (see <see cref="GpuFactAttribute"/>).
    /// </summary>
    public sealed class PropBlobShadowGpuTests
    {
        static void WithScene(Action<Scene3D> body)
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            var f = gpu.GpuDevice.Factory;
            using IGpuTexture tex = f.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, tex);
            using var scene = new Scene3D(gpu.GpuDevice, fb.Outputs);
            body(scene);
        }

        [GpuFact]
        public void DrawProps_registers_no_blob_at_the_default_Off_tier() => WithScene(scene =>
        {
            MeshHandle mesh = scene.LoadMesh(MeshPrimitives.Box(1f));
            var meshes = new Dictionary<string, MeshHandle> { ["pine_a"] = mesh };
            var radii = new Dictionary<string, float> { ["pine_a"] = 1.5f };
            var placements = new List<PropPlacement> { new("pine_a", 0f, 0f, 0f, 1f, 0f, 0) };

            Assert.Equal(ShadowMode.Off, scene.ResolvedShadowMode);   // default, unchanged
            scene.DrawProps(placements, meshes, Vector3.Zero, drawRadius: 40f, blobRadii: radii);

            Assert.Equal(0, scene.ShadowBlobCount);
        });

        [GpuFact]
        public void DrawProps_registers_a_blob_once_the_resolved_tier_is_Blob() => WithScene(scene =>
        {
            MeshHandle mesh = scene.LoadMesh(MeshPrimitives.Box(1f));
            var meshes = new Dictionary<string, MeshHandle> { ["pine_a"] = mesh };
            var radii = new Dictionary<string, float> { ["pine_a"] = 1.5f };
            var placements = new List<PropPlacement> { new("pine_a", 0f, 0f, 0f, 1f, 0f, 0) };

            scene.Post.Quality.Shadows.Mode = ShadowMode.Blob;
            Assert.Equal(ShadowMode.Blob, scene.ResolvedShadowMode);

            scene.DrawProps(placements, meshes, Vector3.Zero, drawRadius: 40f, blobRadii: radii);

            Assert.Equal(1, scene.ShadowBlobCount);
        });

        [GpuFact]
        public void DrawProps_with_no_blob_table_registers_nothing_even_at_Blob_tier() => WithScene(scene =>
        {
            // Defaults-inert: a layer with no BlobRadii (the default on every factory) must queue nothing at any
            // tier, including Blob, so every existing scene stays byte-stable until it opts a kit in.
            MeshHandle mesh = scene.LoadMesh(MeshPrimitives.Box(1f));
            var meshes = new Dictionary<string, MeshHandle> { ["pine_a"] = mesh };
            var placements = new List<PropPlacement> { new("pine_a", 0f, 0f, 0f, 1f, 0f, 0) };

            scene.Post.Quality.Shadows.Mode = ShadowMode.Blob;
            scene.DrawProps(placements, meshes, Vector3.Zero, drawRadius: 40f);   // no blobRadii

            Assert.Equal(0, scene.ShadowBlobCount);
        });

        // A flat meadow: uniform height everywhere, so a single-kind ScatterConfig places deterministically and
        // every placement is easy to reason about (mirrors Scene3DChunkSinkTests' Flat/OneKind helpers).
        static TerrainField Flat(float height) => new TerrainField(new TerrainConfig
        {
            GentleAmplitude = 0f,
            WaterLevel = 0f,
            Biomes = new[]
            {
                new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, Biome = BiomeId.Meadow, BaseHeight = height, HillAmplitude = 0f },
            },
        });

        static ScatterConfig OneKind(string id, int seed, float cell) => new ScatterConfig
        {
            Seed = seed,
            CellSize = cell,
            Jitter = 0.5f,
            ClearingRadius = 0f,
            MaxHeight = null,
            Biomes = new[]
            {
                new BiomeScatterRule { Biome = BiomeId.Meadow, Density = 1f, Kinds = new[] { new PropKind(id, 1f) } },
            },
        };

        [GpuFact]
        public void Sink_Draw_registers_no_blob_once_the_cluster_has_swapped_to_its_merged_hlod_mesh() => WithScene(scene =>
        {
            // Hard HLOD swap (default crossfade width 0): past HlodDistance a chunk cluster draws ONLY the merged
            // mesh (Scene3DChunkSink.Draw's hlodHandle branch), never the individual placements, so a layer's blob
            // table - even with an entry for this kit, even at the Blob tier - must register nothing for a focus
            // point on the far side of the swap boundary.
            scene.Post.Quality.Shadows.Mode = ShadowMode.Blob;

            MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1f));
            var meshes = new Dictionary<string, MeshHandle> { ["pine_a"] = box };
            var radii = new Dictionary<string, float> { ["pine_a"] = 1.5f };
            var sourceMeshes = new Dictionary<string, GltfMesh> { ["pine_a"] = MeshPrimitives.Sphere(radius: 1f, rings: 8, segments: 10) };

            TerrainField field = Flat(0f);
            ScatterConfig scatter = OneKind("pine_a", seed: 3, cell: 6f);
            PropLayer layer = PropLayer.ScatterLayer(scatter, meshes, drawRadius: 90f, blobRadii: radii)
                .WithHlod(sourceMeshes, hlodDistance: 50f, weldCell: 2f);   // crossfadeWidth 0 = hard swap
            var sink = new Scene3DChunkSink(scene, field, new[] { layer }, chunkSize: 60f);
            var coord = new ChunkCoord(0, 0);
            sink.Load(coord, lod: 0);

            // Chunk (0,0)'s centre sits at (30, 30) for a 60m chunk: a focus 500m away puts chunk-centre distance
            // (~640m) far past HlodDistance (50m), so this cluster is fully swapped to its merged mesh (t = 1).
            var farFocus = new Vector3(500f, 0f, 500f);
            sink.Draw(farFocus);

            Assert.Equal(0, scene.ShadowBlobCount);
        });

        [GpuFact]
        public void Sink_Draw_registers_blobs_for_individual_props_before_the_hlod_swap() => WithScene(scene =>
        {
            // Sanity companion to the swap test above: with the SAME layer and a focus that keeps the cluster below
            // HlodDistance (t = 0, individual props draw), the blob table DOES fire, so the swap test above is
            // actually proving something (not just a config that never blobs at all).
            scene.Post.Quality.Shadows.Mode = ShadowMode.Blob;

            MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1f));
            var meshes = new Dictionary<string, MeshHandle> { ["pine_a"] = box };
            var radii = new Dictionary<string, float> { ["pine_a"] = 1.5f };
            var sourceMeshes = new Dictionary<string, GltfMesh> { ["pine_a"] = MeshPrimitives.Sphere(radius: 1f, rings: 8, segments: 10) };

            TerrainField field = Flat(0f);
            ScatterConfig scatter = OneKind("pine_a", seed: 3, cell: 6f);
            PropLayer layer = PropLayer.ScatterLayer(scatter, meshes, drawRadius: 90f, blobRadii: radii)
                .WithHlod(sourceMeshes, hlodDistance: 50f, weldCell: 2f);
            var sink = new Scene3DChunkSink(scene, field, new[] { layer }, chunkSize: 60f);
            var coord = new ChunkCoord(0, 0);
            sink.Load(coord, lod: 0);

            sink.Draw(Vector3.Zero);   // chunk centre (30, 30) is well inside HlodDistance (50m)

            Assert.True(scene.ShadowBlobCount > 0);
        });
    }
}
