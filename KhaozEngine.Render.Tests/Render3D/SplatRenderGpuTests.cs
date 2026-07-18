using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public sealed class SplatRenderGpuTests
    {
        static List<SplatLayerImage> FiveSolidLayers(int size)
        {
            var layers = new List<SplatLayerImage>();
            byte[][] colors =
            {
                new byte[] { 60, 110, 40, 255 },   // grass
                new byte[] { 90, 75, 55, 255 },    // dirt
                new byte[] { 110, 105, 100, 255 }, // rock
                new byte[] { 190, 175, 125, 255 }, // sand
                new byte[] { 235, 238, 245, 255 }, // snow
            };
            foreach (var c in colors)
            {
                var albedo = new byte[size * size * 4];
                var normal = new byte[size * size * 4];
                for (int p = 0; p < albedo.Length; p += 4)
                {
                    albedo[p] = c[0]; albedo[p + 1] = c[1]; albedo[p + 2] = c[2]; albedo[p + 3] = 255;
                    normal[p] = 128; normal[p + 1] = 128; normal[p + 2] = 255; normal[p + 3] = 255; // flat
                }
                layers.Add(new SplatLayerImage { AlbedoRgba = albedo, NormalRgba = normal, TilesPerMetre = 0.25f, Roughness = 0.8f });
            }
            return layers;
        }

        [GpuFact]
        public void SplatTerrainMeshRendersWithoutThrowing()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            var f = gd.Factory;
            const int W = 64, H = 48;
            using IGpuTexture finalTex = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer finalFB = f.CreateFramebuffer(null, finalTex);
            using var scene = new Scene3D(gd, finalFB.Outputs);
            using IGpuCommandList cl = f.CreateCommandList();

            var mat = scene.LoadSplatMaterial(8, 8, FiveSolidLayers(8));

            // A flat quad on the ground; Color carries packed weights (all grass here: (1,0,0,0)).
            var w = new Vector4(1f, 0f, 0f, 0f);
            var verts = new[]
            {
                new ModelVertex(new Vector3(-1, 0, -1), Vector3.UnitY, w, new Vector2(0, 0)),
                new ModelVertex(new Vector3( 1, 0, -1), Vector3.UnitY, w, new Vector2(1, 0)),
                new ModelVertex(new Vector3( 1, 0,  1), Vector3.UnitY, w, new Vector2(1, 1)),
                new ModelVertex(new Vector3(-1, 0,  1), Vector3.UnitY, w, new Vector2(0, 1)),
            };
            var mesh = new GltfMesh(verts, new ushort[] { 0, 1, 2, 0, 2, 3 });
            var handle = scene.LoadMesh(mesh, mat);

            scene.Begin();
            scene.Draw(handle, Matrix4x4.Identity, Color.White);
            cl.Begin();
            scene.RenderInternal(cl, W, H, finalFB);
            cl.End();
            gd.Submit(cl);
            gd.WaitForIdle();

            scene.UnloadMesh(handle);
            scene.UnloadSplatMaterial(mat);
        }

        // Dispose-order guard: Scene3D.Dispose clears the backing splat-material list, so a caller that still holds
        // a handle after the scene is gone (a sink's ownsMaterial teardown, or a ViewportWorld disposed after its
        // owning scene, see ViewportWorldDisposeOrderGpuTests) used to index past the end of that now-empty list and
        // throw ArgumentOutOfRangeException. It must be a silent no-op instead.
        [GpuFact]
        public void UnloadSplatMaterial_AfterSceneDisposed_IsSafeNoOp()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            var f = gd.Factory;
            using IGpuTexture finalTex = f.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer finalFB = f.CreateFramebuffer(null, finalTex);
            var scene = new Scene3D(gd, finalFB.Outputs);

            var mat = scene.LoadSplatMaterial(8, 8, FiveSolidLayers(8));

            scene.Dispose();               // the owning scene, and every splat material it holds, is torn down first
            scene.UnloadSplatMaterial(mat); // a caller still holding the handle must not throw
        }
    }
}
