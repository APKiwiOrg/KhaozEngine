using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    // Scene3D's ctor needs a GPU device, so these run gated behind KE_GPU_TESTS=1 (mirrors Scene3DTextureUnloadTests).
    // They prove Scene3DChunkSink.Dispose() frees every loaded chunk's GPU mesh so tearing the sink down while the
    // SAME Scene3D survives (level change / world reload / teleport rebuild) doesn't leak a full ring of terrain
    // meshes - and that the caller-owned splat material is freed only when the opt-in ownsMaterial flag is set.
    public sealed class Scene3DChunkSinkDisposeGpuTests
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

        static IReadOnlyDictionary<string, MeshHandle> NoMeshes() => new Dictionary<string, MeshHandle>();
        static readonly ChunkCoord[] Ring = { new(0, 0), new(1, 0), new(0, 1), new(-1, 0) };

        [GpuFact]
        public void Dispose_unloads_every_loaded_chunk_mesh() => WithScene(scene =>
        {
            var field = new TerrainField(TerrainPresets.Clearing());
            int baseMeshes = scene.LiveMeshCount;
            var sink = new Scene3DChunkSink(scene, field, ScatterConfig.ForestRing(), NoMeshes(),
                chunkSize: 60f, propDrawRadius: 90f);

            foreach (ChunkCoord c in Ring) sink.Load(c, lod: 0);
            Assert.Equal(baseMeshes + Ring.Length, scene.LiveMeshCount);   // one GPU terrain mesh per loaded chunk

            sink.Dispose();
            Assert.Equal(baseMeshes, scene.LiveMeshCount);                 // the whole ring freed, no leak
        });

        [GpuFact]
        public void Dispose_is_idempotent() => WithScene(scene =>
        {
            var field = new TerrainField(TerrainPresets.Clearing());
            int baseMeshes = scene.LiveMeshCount;
            var sink = new Scene3DChunkSink(scene, field, ScatterConfig.ForestRing(), NoMeshes(),
                chunkSize: 60f, propDrawRadius: 90f);
            sink.Load(new ChunkCoord(0, 0), lod: 0);

            sink.Dispose();
            sink.Dispose();                                               // second dispose is a no-op (no double-free throw)
            Assert.Equal(baseMeshes, scene.LiveMeshCount);
        });

        [GpuFact]
        public void Dispose_keeps_the_caller_owned_material_by_default() => WithScene(scene =>
        {
            var field = new TerrainField(TerrainPresets.Clearing());
            int baseMeshes = scene.LiveMeshCount, baseMats = scene.LiveSplatMaterialCount;

            Scene3D.SplatMaterialHandle mat = scene.LoadTerrainMaterial(TerrainMaterialPresets.Procedural(32));
            Assert.Equal(baseMats + 1, scene.LiveSplatMaterialCount);

            var sink = new Scene3DChunkSink(scene, field, ScatterConfig.ForestRing(), NoMeshes(),
                chunkSize: 60f, propDrawRadius: 90f, material: mat);   // ownsMaterial defaults to false
            sink.Load(new ChunkCoord(0, 0), lod: 0);
            sink.Dispose();

            Assert.Equal(baseMeshes, scene.LiveMeshCount);                // meshes freed
            Assert.Equal(baseMats + 1, scene.LiveSplatMaterialCount);     // material is caller-owned: NOT freed
            scene.UnloadSplatMaterial(mat);                              // caller frees it
            Assert.Equal(baseMats, scene.LiveSplatMaterialCount);
        });

        [GpuFact]
        public void Dispose_frees_the_material_when_ownsMaterial_is_set() => WithScene(scene =>
        {
            var field = new TerrainField(TerrainPresets.Clearing());
            int baseMats = scene.LiveSplatMaterialCount;

            Scene3D.SplatMaterialHandle mat = scene.LoadTerrainMaterial(TerrainMaterialPresets.Procedural(32));
            var sink = new Scene3DChunkSink(scene, field, ScatterConfig.ForestRing(), NoMeshes(),
                chunkSize: 60f, propDrawRadius: 90f, material: mat, ownsMaterial: true);
            sink.Load(new ChunkCoord(0, 0), lod: 0);

            sink.Dispose();
            Assert.Equal(baseMats, scene.LiveSplatMaterialCount);        // owned material freed alongside the meshes
        });
    }
}
