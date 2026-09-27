using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A STEADY FRAME THAT DID NOT REQUEST COUNTS ALLOCATES NOTHING AND READS NOTHING BACK, even once the count probe
    /// exists. Each frame renders the main view, which resolves and runs the display chain with its default sharpen,
    /// then a capture at the same size, both with bloom on. One request during the warm-up builds the probe and is
    /// read back at the PrepareFrame after it. The measured frames make no request.
    /// </summary>
    [Collection("AllocSensitive")]
    public sealed class TemporalCountProbeAllocationGpuTests(ITestOutputHelper output)
    {
        const int W = 320, H = 180, Warm = 8, Measured = 16;

        [GpuFact]
        public void A_steady_frame_without_a_request_allocates_nothing_and_reads_nothing_back()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            using IGpuTexture display = Target(gd), capture = Target(gd);
            using IGpuFramebuffer displayFb = gd.Factory.CreateFramebuffer(null, display);
            using IGpuFramebuffer captureFb = gd.Factory.CreateFramebuffer(null, capture);
            using IGpuCommandList cl = gd.Factory.CreateCommandList();
            using var scene = new Scene3D(gd, displayFb.Outputs);
            scene.Post.UseSmoothPreset();
            scene.Post.Bloom.Enabled = true;
            scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            scene.Post.Temporal.Upscale = TemporalUpscale.Quality;
            scene.Camera.OrthoSize = 10f;
            MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1f));
            int n = 0;

            void Frame()
            {
                scene.Begin();
                scene.EffectTimeSeconds = n / 60f;
                scene.Camera.Target = new Vector3(0.05f * n, 0f, 0f);
                scene.Draw(box, Matrix4x4.CreateScale(40f, 0.2f, 40f) * Matrix4x4.CreateTranslation(0f, -0.6f, 0f));
                scene.Draw(box, Matrix4x4.CreateTranslation(0f, 0.5f, 0f), new Color(3f, 2.5f, 1.5f, 1f));
                scene.DrawBeam(new Vector3(-4f, 1f, 0f), new Vector3(4f, 1f, 0f), 0.3f, Color.White);
                scene.PrepareFrame();
                using (GpuRecording.Open(gd, cl, nameof(TemporalCountProbeAllocationGpuTests)))
                    scene.RenderInternal(cl, W, H, displayFb);
                gd.Submit(cl);
                using (GpuRecording.Open(gd, cl, nameof(TemporalCountProbeAllocationGpuTests)))
                    scene.RenderInternal(cl, W, H, captureFb);
                gd.Submit(cl);
                gd.WaitForIdle();
                n++;
            }
            void Frames(int count) { for (int i = 0; i < count; i++) Frame(); }

            Frames(2);
            scene.RequestTemporalCounts();
            Frames(Warm);   // the first arms and records the probe, the second's PrepareFrame reads it back
            TemporalDiagnostics warm = scene.LastTemporalDiagnostics;
            Assert.True(scene.TemporalCountProbeBuiltForTests);
            Assert.Equal(1, scene.TemporalCountReadbacksForTests);
            Assert.Equal(warm.FrameIndex - (Warm - 1), warm.CountsFrameIndex);   // the first warm frame
            Assert.True(scene.TemporalSharpenBuiltForTests && !scene.LaterRenderSharpenBuiltForTests,
                "the measured frames must run the default sharpen on the display chain and not on the capture's");
            Assert.True(scene.TemporalPostTargetsForTests!.BloomAllocated && scene.BloomAllocated);
            Assert.True(warm.HistoryValid);

            long first = Allocated(() => Frames(Measured));
            long retry = first == 0 ? 0 : Allocated(() => Frames(Measured));   // one retry, as AllocAssert allows
            TemporalDiagnostics steady = scene.LastTemporalDiagnostics;
            output.WriteLine($"{Measured} steady resolving frames with a capture each and the probe built: {first} "
                + $"bytes, retry {retry}, readbacks {scene.TemporalCountReadbacksForTests}");
            Assert.True(retry == 0,
                $"{Measured} steady frames without a request allocated {first} bytes and {retry} on the retry");
            Assert.Equal(1, scene.TemporalCountReadbacksForTests);
            Assert.Equal(warm.CountsFrameIndex, steady.CountsFrameIndex);

            // For the record only: a request's frame and the harvest after it.
            scene.RequestTemporalCounts();
            long requested = Allocated(() => Frames(2));
            output.WriteLine($"a requested frame and the frame that reads it back: {requested} bytes");
            Assert.Equal(2, scene.TemporalCountReadbacksForTests);
        }

        static IGpuTexture Target(IGpuDevice gd) => gd.Factory.CreateTexture(GpuTextureDescription.Texture2D(W, H,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));

        static long Allocated(Action frames)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            frames();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }
}
