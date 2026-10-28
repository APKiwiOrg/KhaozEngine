using System;
using System.Numerics;
using System.Runtime.Versioning;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// TEMPORAL-RESOLVE-UPSCALING-DESIGN section 7 item 10: a steady frame with temporal anti-aliasing, the default
    /// sharpen and a keyed mover allocates nothing, with bloom off and on, and with and without a resolve debug view.
    /// Measured over the fake device, which allocates nothing of its own, and on the real Metal device over the
    /// recording, over the whole headless frame, and over the whole frame presented through a swapchain.
    /// </summary>
    [Collection("AllocSensitive")]   // a zero-allocation reading measures its neighbours too (#264)
    public sealed class TemporalSteadyFrameAllocationTests(ITestOutputHelper output)
    {
        const int Warm = 6, Measured = 20;

        [Theory]
        [InlineData(SceneDebugView.None, false, 0)]
        [InlineData(SceneDebugView.None, false, 1)]
        [InlineData(SceneDebugView.None, true, 0)]
        [InlineData(SceneDebugView.None, true, 1)]
        [InlineData(SceneDebugView.History, false, 0)]
        [InlineData(SceneDebugView.History, false, 1)]
        [InlineData(SceneDebugView.History, true, 0)]
        [InlineData(SceneDebugView.History, true, 1)]
        [InlineData(SceneDebugView.Shimmer, false, 0)]
        [InlineData(SceneDebugView.Shimmer, false, 1)]
        [InlineData(SceneDebugView.Shimmer, true, 0)]
        [InlineData(SceneDebugView.Shimmer, true, 1)]
        public void ASteadyTemporalFrameAllocatesNothing(SceneDebugView view, bool bloom, int choice)
        {
            using var device = new FakeGpuDevice();
            var factory = (FakeGpuResourceFactory)device.Factory;
            using IGpuTexture targetTexture = factory.CreateTexture(GpuTextureDescription.Texture2D(
                160, 90, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer target = factory.CreateFramebuffer(null, targetTexture);
            using var scene = new Scene3D(device, target.Outputs, new ShadowSettings { Mode = ShadowMode.Off });
            scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            scene.Post.Temporal.Upscale = TemporalUpscale.Quality;
            scene.Post.Bloom.Enabled = bloom;
            scene.DebugView = view;
            scene.TemporalResolveEntryForTests = (TemporalResolveEntry)choice;
            MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1f));
            using IGpuCommandList commands = factory.CreateCommandList();
            int frame = 0;

            void Frame()
            {
                scene.Begin();
                scene.Draw(box, Matrix4x4.Identity);
                scene.Draw(new RigidInstanceDraw(box, Matrix4x4.CreateTranslation(0.01f * frame++, 0f, 2f))
                { Motion = MotionKey.From(5) });
                scene.PrepareFrame();
                commands.Begin();
                scene.RenderInternal(commands, 160, 90, target);
                commands.End();
            }

            for (int i = 0; i < Warm; i++) Frame();   // size every grow-only buffer, build every lazy pass
            AssertTheFrameIsTheMeasuredOne(scene, view, bloom);
            string what = $"{Measured} steady temporal frames, debug view {view}, bloom {bloom}";
            AllocAssert.NoPerCallAllocation(what, () =>
            {
                for (int i = 0; i < Measured; i++) Frame();
            });
        }

        [GpuTheory]
        [InlineData(false, 0)]
        [InlineData(true, 0)]
        [InlineData(false, 1)]
        [InlineData(true, 1)]
        public void RecordingASteadyTemporalFrameOnTheDeviceAllocatesNothing(bool bloom, int choice)
        {
            if (GpuBackendSelector.Select() != GpuBackendKind.MetalNative)
            {
                output.WriteLine("measured on the Metal leg only");
                return;
            }
            const int W = 320, H = 180;
            var stage = new FrontStage(W, H, 4.5f);
            using var fx = new TemporalFixture(W, H, s =>
            {
                stage.Setup(s, AntiAliasing.Temporal, TemporalUpscale.Quality);
                s.Post.Bloom.Enabled = bloom;
            });
            Scene3D scene = fx.Scene;
            scene.TemporalResolveEntryForTests = (TemporalResolveEntry)choice;
            IGpuDevice gd = fx.Device;
            using IGpuCommandList cl = gd.Factory.CreateCommandList();
            int n = 0;
            long recorded = 0;

            void Frame()
            {
                scene.Begin();
                scene.EffectTimeSeconds = n / 60f;
                stage.Wall(scene);
                scene.Draw(new RigidInstanceDraw(stage.Box, Matrix4x4.CreateTranslation(0.01f * n++, 0f, 0f))
                { Motion = MotionKey.From(3) });
                scene.PrepareFrame();
                long before = GC.GetAllocatedBytesForCurrentThread();
                using (GpuRecording.Open(gd, cl, nameof(TemporalSteadyFrameAllocationTests)))
                    scene.RenderInternal(cl, W, H, fx.Target);
                recorded += GC.GetAllocatedBytesForCurrentThread() - before;
                gd.Submit(cl);
                gd.WaitForIdle();
            }
            void Frames(int count) { for (int i = 0; i < count; i++) Frame(); }

            Frames(Warm);
            AssertTheFrameIsTheMeasuredOne(scene, SceneDebugView.None, bloom);
            recorded = 0;
            long whole = AllocationMeasurement.Measure($"{Measured} whole steady temporal frames", () => Frames(Measured));
            output.WriteLine($"{Measured} steady temporal frames on {gd.Backend}, bloom {bloom}: recording "
                + $"{recorded} bytes, whole frame {whole} bytes");
            Assert.True(recorded == 0, $"recording {Measured} steady temporal frames allocated {recorded} bytes");
            Assert.True(whole == 0, $"{Measured} whole steady temporal frames allocated {whole} bytes");
        }

        /// <summary>
        /// The windowed frame, as far as a test can reach it: a steady temporal frame recorded into a swapchain
        /// framebuffer, submitted and presented with no drain, as <c>FramePhases</c> runs a window's frame, allocates
        /// nothing over the whole frame. The device is built over a <c>CAMetalLayer</c> with no window, so the
        /// acquire, the present and the drawable cycle run for real. What no test can build is the window itself,
        /// which leaves only <c>MetalLayerHost</c>'s Cocoa selectors to a windowed playtest.
        /// </summary>
        [GpuTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void PresentingASteadyTemporalFrameThroughASwapchainAllocatesNothing(bool bloom)
        {
            bool metal = GpuBackendSelector.Select() == GpuBackendKind.MetalNative;
            if (!metal || !MetalDormancy.NativeDeviceAvailable(output))
            {
                output.WriteLine("measured on the Metal leg only");
                return;
            }
            PresentThroughALayer(bloom, output);
        }

        [SupportedOSPlatform("macos")]
        static void PresentThroughALayer(bool bloom, ITestOutputHelper output)
        {
            const uint W = 320, H = 180;
            CAMetalLayer layer = CAMetalLayer.New();
            if (layer.IsNull)
            {
                output.WriteLine("dormant: this macOS would not create a CAMetalLayer");
                return;
            }
            // The layer's ownership moves into the device on success and on a throw alike.
            IGpuDevice gd = MetalGpuDevice.CreateForHost(
                new MetalSwapchainHost(layer, new MetalDrawableSize(W, H)), syncToVerticalBlank: false).Device;
            try
            {
                PresentFrames(gd, bloom, output);
                long skipped = ((MetalGpuDevice)gd).PresentBoundary!.SkippedPresents;
                Assert.True(skipped == 0, $"the swapchain skipped {skipped} presents, so not every frame was shown");
            }
            finally
            {
                gd.WaitForIdle();
                gd.Dispose();
            }
        }

        static void PresentFrames(IGpuDevice gd, bool bloom, ITestOutputHelper output)
        {
            IGpuFramebuffer swapchain = gd.SwapchainFramebuffer!;
            int w = (int)swapchain.Width, h = (int)swapchain.Height;
            using IGpuCommandList cl = gd.Factory.CreateCommandList();
            using var scene = new Scene3D(gd, swapchain.Outputs);
            var stage = new FrontStage(w, h, 4.5f);
            stage.Setup(scene, AntiAliasing.Temporal, TemporalUpscale.Quality);
            scene.Post.Bloom.Enabled = bloom;
            scene.RenderOrigin = Vector3.Zero;
            int n = 0;

            void Frame()
            {
                scene.Begin();
                scene.EffectTimeSeconds = n / 60f;
                stage.Wall(scene);
                scene.Draw(new RigidInstanceDraw(stage.Box, Matrix4x4.CreateTranslation(0.01f * n++, 0f, 0f))
                { Motion = MotionKey.From(3) });
                scene.PrepareFrame();
                using (GpuRecording.Open(gd, cl, nameof(TemporalSteadyFrameAllocationTests)))
                    scene.RenderInternal(cl, w, h, swapchain);
                gd.Submit(cl);
                gd.Present();
            }
            void Frames(int count) { for (int i = 0; i < count; i++) Frame(); }

            Frames(Warm);
            gd.WaitForIdle();
            AssertTheFrameIsTheMeasuredOne(scene, SceneDebugView.None, bloom);
            long begun = gd.Counters.FramesBegun;
            long allocated = AllocationMeasurement.Measure($"{Measured} presented steady temporal frames",
                () => Frames(Measured));
            gd.WaitForIdle();
            long presented = gd.Counters.FramesBegun - begun;
            output.WriteLine($"{Measured} steady temporal frames presented through a {w}x{h} swapchain on "
                + $"{gd.Backend}, bloom {bloom}: {allocated} bytes, {presented} frames begun");
            Assert.True(presented >= Measured,
                $"the present boundary opened {presented} frames for {Measured} presents");
            Assert.True(allocated == 0, $"{Measured} presented steady temporal frames allocated {allocated} bytes");
        }

        // The warm frames built what the measured frames run: the resolve, the default sharpen, the display chain's
        // bloom when it is on and the debug view pass when a view is set.
        static void AssertTheFrameIsTheMeasuredOne(Scene3D scene, SceneDebugView view, bool bloom)
        {
            Assert.True(scene.TemporalResolveRendererForTests!.VisibilityHistory.Allocated);
            Assert.Equal(GpuPixelFormat.R8UNorm, scene.TemporalResolveRendererForTests.VisibilityHistory.Texture(0).Format);
            Assert.True(scene.ResolvedLastRenderForTests, "the last warm frame did not run the resolve");
            Assert.True(scene.TemporalSharpenBuiltForTests, "the default sharpen is not in the display chain");
            Assert.Equal(bloom, scene.TemporalPostTargetsForTests!.BloomAllocated);
            Assert.Equal(view != SceneDebugView.None, scene.TemporalDebugViewBuiltForTests);
        }
    }
}
