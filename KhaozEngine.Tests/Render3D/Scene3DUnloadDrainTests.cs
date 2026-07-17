using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    // Regression coverage for the lavapipe mode-2 crash documented alongside the device-lifecycle gate
    // (GpuDeviceContext remarks): Scene3D used to Dispose() a mid-life resource immediately on Unload*, racing any
    // upload or draw still queued on the device (Mesa lavapipe executes queued work on its own thread and segfaults
    // on a resource freed out from under it). Scene3D now drains the device (WaitForIdle) before disposing. These
    // tests prove the DRAIN happens during the unload call via a spy device (the drain-precedes-dispose ordering
    // inside the method is not observable through the device seam, so the drain-during-unload is the pinned
    // contract). Scene3DTextureUnloadTests is the behavioural (slot-freeing) coverage.
    public sealed class Scene3DUnloadDrainTests
    {
        static readonly byte[] Pixel = new byte[] { 255, 255, 255, 255 };   // 1x1 RGBA8

        static (GpuDeviceContext Gpu, SpyGpuDevice Spy, Scene3D Scene, IGpuTexture Target, IGpuFramebuffer Fb) MakeScene()
        {
            GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            var spy = new SpyGpuDevice(gpu.GpuDevice);
            var f = spy.Factory;
            IGpuTexture tex = f.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            IGpuFramebuffer fb = f.CreateFramebuffer(null, tex);
            var scene = new Scene3D(spy, fb.Outputs);
            return (gpu, spy, scene, tex, fb);
        }

        [GpuFact]
        public void UnloadTexture_DrainsTheDevice()
        {
            var (gpu, spy, scene, tex, fb) = MakeScene();
            using (gpu) using (scene) using (tex) using (fb)
            {
                var h = scene.LoadTexture(Pixel, 1, 1);   // 1-mip path: returns with its upload still queued
                int before = spy.WaitForIdleCalls;

                scene.UnloadTexture(h);

                Assert.True(spy.WaitForIdleCalls > before,
                    "UnloadTexture must drain the device (WaitForIdle) before disposing the texture");
            }
        }

        [GpuFact]
        public void UnloadMesh_DrainsTheDevice()
        {
            var (gpu, spy, scene, tex, fb) = MakeScene();
            using (gpu) using (scene) using (tex) using (fb)
            {
                var verts = new[]
                {
                    new ModelVertex(Vector3.Zero, Vector3.UnitZ, Vector4.One),
                    new ModelVertex(Vector3.UnitX, Vector3.UnitZ, Vector4.One),
                    new ModelVertex(Vector3.UnitY, Vector3.UnitZ, Vector4.One),
                };
                var mesh = new GltfMesh(verts, new uint[] { 0, 1, 2 });
                MeshHandle h = scene.LoadMesh(mesh);
                int before = spy.WaitForIdleCalls;

                scene.UnloadMesh(h);

                Assert.True(spy.WaitForIdleCalls > before,
                    "UnloadMesh must drain the device (WaitForIdle) before disposing the buffers");
            }
        }
    }
}
