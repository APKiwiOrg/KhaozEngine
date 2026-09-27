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
    /// A STEADY RESOLVING FRAME ALLOCATES NOTHING WITH A RESOLVE DEBUG VIEW SELECTED OR WITHOUT ONE. Each frame
    /// renders the main view, which resolves, runs the display chain with its default sharpen, and draws the selected
    /// view, then a capture at the same size, which draws no view. Bloom is on in both chains. The view's pass is built
    /// by the first frame, and after that a frame only binds it.
    /// </summary>
    [Collection("AllocSensitive")]
    public sealed class TemporalDebugViewAllocationGpuTests(ITestOutputHelper output)
    {
        const int W = 320, H = 180, Warm = 8, Measured = 16;

        [GpuTheory]
        [InlineData(SceneDebugView.None)]
        [InlineData(SceneDebugView.History)]
        [InlineData(SceneDebugView.Disocclusion)]
        [InlineData(SceneDebugView.Reactive)]
        public void A_steady_resolving_frame_allocates_nothing_whatever_view_is_selected(SceneDebugView view)
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
            scene.DebugView = view;
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
                using (GpuRecording.Open(gd, cl, nameof(TemporalDebugViewAllocationGpuTests)))
                    scene.RenderInternal(cl, W, H, displayFb);
                gd.Submit(cl);
                using (GpuRecording.Open(gd, cl, nameof(TemporalDebugViewAllocationGpuTests)))
                    scene.RenderInternal(cl, W, H, captureFb);
                gd.Submit(cl);
                gd.WaitForIdle();
                n++;
            }
            void Frames(int count) { for (int i = 0; i < count; i++) Frame(); }

            Frames(Warm);
            Assert.Equal(view != SceneDebugView.None, scene.TemporalDebugViewBuiltForTests);
            Assert.True(scene.TemporalSharpenBuiltForTests && !scene.LaterRenderSharpenBuiltForTests,
                "the measured frames must run the default sharpen on the display chain and not on the capture's");
            Assert.True(scene.TemporalPostTargetsForTests!.BloomAllocated && scene.BloomAllocated);
            Assert.True(scene.LastTemporalDiagnostics.HistoryValid);

            long first = Allocated(() => Frames(Measured));
            long retry = first == 0 ? 0 : Allocated(() => Frames(Measured));   // one retry, as AllocAssert allows
            output.WriteLine($"{view}: {Measured} steady resolving frames with a capture each: {first} bytes, "
                + $"retry {retry}");
            Assert.True(retry == 0,
                $"{view}: {Measured} steady resolving frames allocated {first} bytes and {retry} on the retry");
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
