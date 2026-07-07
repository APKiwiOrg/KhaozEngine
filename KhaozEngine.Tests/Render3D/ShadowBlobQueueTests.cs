using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    // The blob-shadow queue lives on a live Scene3D (its ctor needs a GPU device), so this runs gated behind
    // KE_GPU_TESTS=1, mirroring GroundDecalQueueTests. It asserts the per-frame queue accounting only (submitted
    // this frame, cleared next); rendered output is covered by the scene3d_shadow_blob golden.
    public sealed class ShadowBlobQueueTests
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

        static ShadowBlob Sample() => new(new Vector3(1f, 0.5f, 2f), groundY: 0f, radius: 1.5f);

        [GpuFact]
        public void AddShadowBlob_enqueues_and_Begin_clears() => WithScene(scene =>
        {
            Assert.Equal(0, scene.ShadowBlobCount);
            scene.AddShadowBlob(Sample());
            scene.AddShadowBlob(Sample());
            Assert.Equal(2, scene.ShadowBlobCount);
            scene.Begin();
            Assert.Equal(0, scene.ShadowBlobCount);
        });

        [GpuFact]
        public void ResolvedShadows_reflects_mode() => WithScene(scene =>
        {
            Assert.Equal(ShadowMode.Off, scene.ResolvedShadows().Effective);
            scene.Post.Quality.Shadows.Mode = ShadowMode.Blob;
            Assert.Equal(ShadowMode.Blob, scene.ResolvedShadows().Effective);
            scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
            var r = scene.ResolvedShadows();
            Assert.Equal(ShadowMode.Blob, r.Effective);
            Assert.True(r.Degraded);
        });
    }
}
