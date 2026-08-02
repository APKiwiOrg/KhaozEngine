using System.Diagnostics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    // End-to-end check that Scene3D.EnableTiming actually populates PassTimingsMs through a live render, and that
    // leaving it off (the default) leaves every field at 0. Needs a real device (Metal/D3D11/Vulkan), so it is
    // skipped unless KE_GPU_TESTS=1. Byte-stability of rendered pixels with timing on/off is covered by the
    // existing golden suite (timing brackets a Stopwatch only; they never touch what gets drawn).
    public sealed class Scene3DPassTimingsGpuTests
    {
        [GpuFact]
        public void Timing_off_by_default_leaves_every_pass_at_zero()
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

            Assert.False(scene.EnableTiming);
            Render(gd, cl, scene, finalFB, W, H);

            Scene3DPassTimingsMs t = scene.PassTimingsMs;
            Assert.Equal(0f, t.ShadowDepthMs);
            Assert.Equal(0f, t.ModelMs);
            Assert.Equal(0f, t.TransparentsMs);
            Assert.Equal(0f, t.WaterSyncMs);
            Assert.Equal(0f, t.PostMs);
        }

        [GpuFact]
        public void Enabling_timing_populates_model_transparents_and_post()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            var f = gd.Factory;

            const int W = 64, H = 48;
            using IGpuTexture finalTex = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer finalFB = f.CreateFramebuffer(null, finalTex);

            using var scene = new Scene3D(gd, finalFB.Outputs) { EnableTiming = true };
            using IGpuCommandList cl = f.CreateCommandList();

            // Shadows stay Off (default), so ShadowDepthMs legitimately stays 0 - the pass never runs.
            Render(gd, cl, scene, finalFB, W, H);

            Scene3DPassTimingsMs t = scene.PassTimingsMs;
            Assert.Equal(0f, t.ShadowDepthMs);
            Assert.True(t.ModelMs >= 0f);
            Assert.True(t.TransparentsMs >= 0f);
            Assert.Equal(0f, t.WaterSyncMs);   // no water queued, so no ocean prime to report
            Assert.True(t.PostMs > 0f, $"expected a nonzero post-chain encode time, got {t.PostMs}");
        }

        // Issue #374: the ocean FFT's GPU drain used to land inside the transparents bracket as an unmarked
        // Submit+WaitForIdle stall, so a frame with an FFT ocean plane misattributed it as transparents-pass encode
        // cost (measured on the reference scene: 6.70 ms transparents of which 6.57 was the drain). WaterSyncMs
        // carries that span separately. Since #423 the drain is paid in PrepareFrame, before the frame's command
        // list opens, so it is outside the bracket rather than carved out of it - the reported number is the same.
        //
        // ONE frame, deliberately: since #398 the drain exists only on the frame that primes the ocean's row
        // buffers, which is the first one. The steady state has no stall to carve out at all and is the subject of
        // LastWaterStats_OceanStalls_DropToZeroAfterThePrimingFrame below.
        [GpuFact]
        public void Enabling_timing_with_fft_ocean_water_reports_the_sync_stall_separately_from_transparents()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            Assert.True(gd.Capabilities.SupportsCompute, $"{gd.Backend} reports no compute support");
            var f = gd.Factory;

            const int W = 64, H = 48;
            using IGpuTexture finalTex = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer finalFB = f.CreateFramebuffer(null, finalTex);

            using var scene = new Scene3D(gd, finalFB.Outputs) { EnableTiming = true };
            scene.Post.Water.WaveSource = WaterWaveSource.FftOcean;
            using IGpuCommandList cl = f.CreateCommandList();

            scene.Begin();
            scene.DrawWater(new WaterPlane(centerX: 0f, surfaceY: 0f, centerZ: 0f, halfExtentX: 20f));
            // Time the prepare phase itself: the stall it reports has to have been paid inside it.
            long prepareStart = Stopwatch.GetTimestamp();
            scene.PrepareFrame();
            double prepareMs = (Stopwatch.GetTimestamp() - prepareStart) * 1000.0 / Stopwatch.Frequency;
            cl.Begin();
            scene.RenderInternal(cl, W, H, finalFB);
            cl.End();
            gd.Submit(cl);
            gd.WaitForIdle();

            Scene3DPassTimingsMs t = scene.PassTimingsMs;
            Assert.True(t.WaterSyncMs > 0f, $"expected a nonzero ocean FFT sync stall, got {t.WaterSyncMs}");
            // WHERE the stall is paid, which is the half of #374 that #423 changed. The reported span is a
            // sub-interval of PrepareFrame, so the phase cannot have taken less time than the stall it reports.
            // Asserting TransparentsMs >= 0 used to stand here and proved nothing once the subtraction went away.
            Assert.True(prepareMs >= t.WaterSyncMs,
                $"the ocean prime stall ({t.WaterSyncMs:F3} ms) is supposed to be paid inside PrepareFrame, but "
                + $"PrepareFrame only took {prepareMs:F3} ms, so it was paid somewhere else");

            // Same measured stall the always-on water diagnostics surface reports (#374's other half, LastWaterStats).
            Assert.Equal(t.WaterSyncMs, (float)scene.LastWaterStats.OceanStallMs, 3);
        }

        // Issue #374's other exposure: LastClipmapRebuilds was internal-only on WaterRenderer before this, reachable
        // from KhaozEngine's own tests but not from a consuming game. A fresh clipmap grid always rebuilds on the
        // first Draw that uses it (nothing cached yet), so that is enough to prove the public passthrough is wired
        // to the real counter rather than always reading 0.
        [GpuFact]
        public void LastWaterStats_ClipmapRebuilds_ReflectsAFreshGridsFirstRebuild()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            var f = gd.Factory;

            const int W = 64, H = 48;
            using IGpuTexture finalTex = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer finalFB = f.CreateFramebuffer(null, finalTex);

            using var scene = new Scene3D(gd, finalFB.Outputs);
            scene.Post.Water.GridMode = WaterGridMode.Clipmap;
            using IGpuCommandList cl = f.CreateCommandList();

            scene.Begin();
            scene.DrawWater(new WaterPlane(centerX: 0f, surfaceY: 0f, centerZ: 0f, halfExtentX: 400f));
            scene.PrepareFrame();
            cl.Begin();
            scene.RenderInternal(cl, W, H, finalFB);
            cl.End();
            gd.Submit(cl);
            gd.WaitForIdle();

            Assert.True(scene.LastWaterStats.ClipmapRebuilds > 0,
                "expected the first-ever clipmap Draw to rebuild at least one grid");
        }

        /// <summary>
        /// The regression guard for <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/398">#398</see>: the
        /// water draw path drains the device on the frame that PRIMES the FFT ocean's row buffers and never again.
        /// Asserted through the public diagnostics a consuming game reads (<see cref="Scene3D.LastWaterStats"/>),
        /// on a real Scene3D render rather than on the producer in isolation, because what regressed would regress
        /// here: a within-frame read-after-write reintroduced anywhere in the water path shows up as a nonzero
        /// stall on a steady-state frame, whatever produced it.
        /// </summary>
        [GpuFact]
        public void LastWaterStats_OceanStalls_DropToZeroAfterThePrimingFrame()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            Assert.True(gd.Capabilities.SupportsCompute, $"{gd.Backend} reports no compute support");
            var f = gd.Factory;

            const int W = 64, H = 48;
            using IGpuTexture finalTex = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer finalFB = f.CreateFramebuffer(null, finalTex);

            using var scene = new Scene3D(gd, finalFB.Outputs) { EnableTiming = true };
            scene.Post.Water.WaveSource = WaterWaveSource.FftOcean;
            using IGpuCommandList cl = f.CreateCommandList();

            for (int frame = 0; frame < 8; frame++)
            {
                // A moving wave clock, so this is the live case rather than a frozen scene that could hold its
                // surface without producing anything.
                scene.EffectTimeSeconds = frame / 60f;
                scene.Begin();
                scene.DrawWater(new WaterPlane(centerX: 0f, surfaceY: 0f, centerZ: 0f, halfExtentX: 20f));
                scene.PrepareFrame();
                cl.Begin();
                scene.RenderInternal(cl, W, H, finalFB);
                cl.End();
                gd.Submit(cl);
                gd.WaitForIdle();

                WaterFrameStats water = scene.LastWaterStats;
                if (frame == 0)
                {
                    Assert.Equal(1, water.OceanStalls);
                    Assert.True(water.OceanStallMs > 0d, $"the priming frame reported no measured drain ({water.OceanStallMs} ms)");
                }
                else
                {
                    Assert.Equal(0, water.OceanStalls);
                    Assert.Equal(0d, water.OceanStallMs);
                    Assert.Equal(0f, scene.PassTimingsMs.WaterSyncMs);
                }
            }
        }

        /// <summary>
        /// <see cref="Scene3D.PrepareFrame"/> is once per FRAME, and the second call for one <see cref="Scene3D.Begin"/>
        /// is a no-op. It has to be: <see cref="Render3DSurface.Render"/> calls it, so a host that also calls it by
        /// hand would prepare twice, and a second preparation re-advances the wave clock with a zero delta and hands
        /// the frame a surface one frame behind - silently, because nothing about the image says so. Measured
        /// through the ocean stall, which is the one visible consequence: the second preparation on a steady frame
        /// re-primes (the first frame's plan was never recorded, so its rows are correctly dropped) and that costs a
        /// drain a steady frame must not pay.
        /// </summary>
        [GpuFact]
        public void PrepareFrame_called_twice_in_one_frame_prepares_once()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            Assert.True(gd.Capabilities.SupportsCompute, $"{gd.Backend} reports no compute support");
            var f = gd.Factory;

            const int W = 64, H = 48;
            using IGpuTexture finalTex = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer finalFB = f.CreateFramebuffer(null, finalTex);

            using var scene = new Scene3D(gd, finalFB.Outputs);
            scene.Post.Water.WaveSource = WaterWaveSource.FftOcean;
            using IGpuCommandList cl = f.CreateCommandList();

            for (int frame = 0; frame < 3; frame++)
            {
                scene.EffectTimeSeconds = frame / 60f;
                scene.Begin();
                scene.DrawWater(new WaterPlane(centerX: 0f, surfaceY: 0f, centerZ: 0f, halfExtentX: 20f));
                scene.PrepareFrame();
                scene.PrepareFrame();   // the doubled call, e.g. a host that prepares and then hands off to a surface
                cl.Begin();
                scene.RenderInternal(cl, W, H, finalFB);
                cl.End();
                gd.Submit(cl);
                gd.WaitForIdle();

                int expected = frame == 0 ? 1 : 0;
                Assert.Equal(expected, scene.LastWaterStats.OceanStalls);
            }
        }

        [GpuFact]
        public void Enabling_timing_with_shadow_map_populates_shadow_depth()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            var f = gd.Factory;

            const int W = 64, H = 48;
            using IGpuTexture finalTex = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer finalFB = f.CreateFramebuffer(null, finalTex);

            using var scene = new Scene3D(gd, finalFB.Outputs) { EnableTiming = true };
            scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
            using IGpuCommandList cl = f.CreateCommandList();

            Render(gd, cl, scene, finalFB, W, H);

            Assert.True(scene.PassTimingsMs.ShadowDepthMs > 0f);
        }

        static void Render(IGpuDevice gd, IGpuCommandList cl, Scene3D scene, IGpuFramebuffer target, int w, int h)
        {
            scene.Begin();
            scene.PrepareFrame();
            cl.Begin();
            scene.RenderInternal(cl, w, h, target);
            cl.End();
            gd.Submit(cl);
            gd.WaitForIdle();
        }
    }
}
