using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    // The water-plane queue lives on a live Scene3D (its ctor needs a GPU device), so this runs gated behind
    // KE_GPU_TESTS=1, mirroring ShadowBlobQueueTests / GroundDecalQueueTests. It asserts the per-frame queue
    // accounting only (submitted this frame, cleared next) and the settings default; rendered output is covered
    // by the scene3d_water golden.
    public sealed class WaterQueueTests
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

        static WaterPlane Sample() => new(centerX: 1f, surfaceY: 0.2f, centerZ: -2f, halfExtentX: 5f);

        [GpuFact]
        public void DrawWater_enqueues_and_Begin_clears() => WithScene(scene =>
        {
            Assert.Equal(0, scene.WaterPlaneCount);
            scene.DrawWater(Sample());
            scene.DrawWater(Sample());
            Assert.Equal(2, scene.WaterPlaneCount);
            scene.Begin();
            Assert.Equal(0, scene.WaterPlaneCount);
        });

        [GpuFact]
        public void No_DrawWater_call_means_no_request_queued() => WithScene(scene =>
        {
            // The opt-in invariant at the queue level: a scene that never calls DrawWater carries zero requests
            // across Begin() calls, so RenderInternal's `if (_waterPlanes.Count > 0)` gate never fires and the
            // water pass never runs (existing scenes byte-stable).
            scene.Begin();
            Assert.Equal(0, scene.WaterPlaneCount);
            scene.Begin();
            Assert.Equal(0, scene.WaterPlaneCount);
        });

        [GpuFact]
        public void Settings_default_off_looking_knobs_are_sane() => WithScene(scene =>
        {
            var w = scene.Post.Water;
            Assert.True(w.Opacity > 0f);
            Assert.True(w.WaveScale > 0f);
            Assert.True(w.NormalStrength >= 0f);
            Assert.True(w.GlintStrength >= 0f);
            Assert.True(w.ShoreFadeDistance >= 0f);
        });
    }
}
