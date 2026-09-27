using System;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// The mip bias the scene writes into the frame block's Params.z and Params.w lanes. Only a render the temporal
    /// resolve accumulates takes it: the first render of a resolving frame. Every other render writes exact zeros, a
    /// frame without temporal anti-aliasing, a temporal frame that does not resolve and a later render of a resolving
    /// frame alike, because no accumulation absorbs a negative bias there. The bias reads the frame's latched display
    /// over internal scale, the unrounded one with the render cap that also sets the jitter cycle. Headless, on
    /// <see cref="HeadlessSceneRig"/>.
    /// </summary>
    public sealed class TemporalMaterialLodSceneTests
    {
        const int BiasOffset = 120, GradScaleOffset = 124;   // Params.z and Params.w of the frame header

        static (float Bias, float GradScale) Lod(Scene3D scene)
        {
            ReadOnlySpan<byte> frameBlock = scene.FrameImageForTests;
            return (BitConverter.ToSingle(frameBlock.Slice(BiasOffset, 4)),
                BitConverter.ToSingle(frameBlock.Slice(GradScaleOffset, 4)));
        }

        static void AssertExactZeros(Scene3D scene, string what)
        {
            ReadOnlySpan<byte> frameBlock = scene.FrameImageForTests;
            Assert.True(BitConverter.ToInt32(frameBlock.Slice(BiasOffset, 4)) == 0, $"{what}: Params.z is not +0");
            Assert.True(BitConverter.ToInt32(frameBlock.Slice(GradScaleOffset, 4)) == 0, $"{what}: Params.w is not +0");
        }

        static void AssertBias(Scene3D scene, double bias)
        {
            var (z, w) = Lod(scene);
            Assert.Equal(bias, z, 1e-5);
            Assert.Equal(Math.Pow(2.0, z) - 1.0, w, 1e-6);
        }

        static HeadlessSceneRig TemporalRig(TemporalUpscale preset)
        {
            var rig = new HeadlessSceneRig();
            rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            rig.Scene.Post.Temporal.Upscale = preset;
            return rig;
        }

        [Fact]
        public void AResolvingFrameWritesThePresetsBias()
        {
            using HeadlessSceneRig rig = TemporalRig(TemporalUpscale.Performance);
            rig.Frame(96, 64);
            Assert.True(rig.Scene.ResolvedLastRenderForTests);
            var (bias, gradScale) = Lod(rig.Scene);
            Assert.Equal(-1.5f, bias);
            Assert.Equal(MathF.Pow(2f, -1.5f) - 1f, gradScale, 1e-6f);
        }

        /// <summary>A capped internal size renders fewer texels than the preset says, so the bias follows the capped
        /// scale: 320 by 180 at Performance is 160 by 90 before a 100 by 100 cap, which scales it by 0.625.</summary>
        [Fact]
        public void TheBiasIncludesTheRenderCap()
        {
            using HeadlessSceneRig rig = TemporalRig(TemporalUpscale.Performance);
            rig.Scene.Post.MaxRenderWidth = 100;
            rig.Scene.Post.MaxRenderHeight = 100;
            rig.Frame(320, 180);
            Assert.Equal((100, 56), (rig.Scene.RenderTargetWidth, rig.Scene.RenderTargetHeight));
            double capped = -Math.Log2(1.0 / (0.5 * 0.625)) - 0.5;
            AssertBias(rig.Scene, capped);
            Assert.NotEqual(-2.0, Lod(rig.Scene).Bias, 2);   // the bare preset's bias
            AssertBias(rig.Scene, -Math.Log2(rig.Scene.TemporalDisplayOverInternal(320, 180)) - 0.5);
        }

        /// <summary>97 by 61 at Quality rounds to a 65 by 41 target, a size ratio whose log2 misses the scale's by
        /// 0.01. The bias reads the unrounded scale the jitter cycle reads.</summary>
        [Fact]
        public void TheBiasReadsTheUnroundedScaleNotTheRoundedTargetSize()
        {
            using HeadlessSceneRig rig = TemporalRig(TemporalUpscale.Quality);
            rig.Frame(97, 61);
            Assert.Equal((65, 41), (rig.Scene.RenderTargetWidth, rig.Scene.RenderTargetHeight));
            AssertBias(rig.Scene, -Math.Log2(1.5) - 0.5);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void TemporalAntiAliasingOffWritesExactZeros(int mode)
        {
            using var rig = new HeadlessSceneRig();
            rig.Scene.Post.Temporal.Upscale = TemporalUpscale.Performance;
            rig.Scene.Post.Quality.AntiAliasing = mode switch
            {
                0 => AntiAliasing.Off,
                1 => AntiAliasing.Fxaa,
                2 => AntiAliasing.Ssaa(2f),
                _ => AntiAliasing.Temporal,
            };
            rig.Scene.Post.Pixelated = mode == 3;   // Pixelated refuses temporal anti-aliasing
            for (int i = 0; i < 2; i++) rig.Frame(96, 64);
            Assert.False(rig.Scene.ResolvedLastRenderForTests);
            AssertExactZeros(rig.Scene, $"mode {mode}");
        }

        [Theory]
        [InlineData(SceneDebugViewTests.Requester.DebugView)]
        [InlineData(SceneDebugViewTests.Requester.TestSeam)]
        public void ATemporalFrameThatDoesNotResolveWritesExactZeros(SceneDebugViewTests.Requester requester)
        {
            using var rig = new HeadlessSceneRig();
            rig.Scene.Post.Temporal.Upscale = TemporalUpscale.Performance;
            if (requester == SceneDebugViewTests.Requester.DebugView)
                rig.Scene.DebugView = SceneDebugView.MotionVectors;
            else
                rig.Scene.ForceTemporalForTests = true;
            for (int i = 0; i < 2; i++) rig.Frame(96, 64);
            Assert.True(rig.Scene.TemporalActive);
            Assert.False(rig.Scene.ResolvedLastRenderForTests);
            AssertExactZeros(rig.Scene, requester.ToString());
        }

        [Theory]
        [InlineData(96, 64)]
        [InlineData(48, 40)]
        public void ALaterRenderOfAResolvingFrameWritesExactZeros(int width, int height)
        {
            using HeadlessSceneRig rig = TemporalRig(TemporalUpscale.Performance);
            rig.Frame(96, 64);
            rig.Frame(96, 64);
            AssertBias(rig.Scene, -1.5);

            rig.Render(width, height);   // a capture inside the same frame
            Assert.False(rig.Scene.ResolvedLastRenderForTests);
            AssertExactZeros(rig.Scene, "the later render");

            rig.Frame(96, 64);
            AssertBias(rig.Scene, -1.5);
        }

        /// <summary>The frame's resolve decision is fixed by its first render, so temporal anti-aliasing selected after
        /// it leaves a later render in that frame unbiased, and the next frame takes the bias.</summary>
        [Fact]
        public void TemporalSelectedAfterTheFirstRenderWaitsForTheNextFrame()
        {
            using var rig = new HeadlessSceneRig();
            rig.Scene.Post.Temporal.Upscale = TemporalUpscale.Performance;
            rig.Frame(96, 64);
            rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            rig.Render(96, 64);
            AssertExactZeros(rig.Scene, "the later render");
            rig.Frame(96, 64);
            AssertBias(rig.Scene, -1.5);
        }

        /// <summary>A preset or offset change after the frame's first render reaches no render of that frame. The next
        /// frame's first render takes both.</summary>
        [Fact]
        public void APresetOrOffsetChangeAfterTheFirstRenderWaitsForTheNextFrame()
        {
            using HeadlessSceneRig rig = TemporalRig(TemporalUpscale.Performance);
            rig.Frame(96, 64);
            AssertBias(rig.Scene, -1.5);
            rig.Scene.Post.Temporal.Upscale = TemporalUpscale.Quality;
            rig.Scene.Post.Temporal.MipBiasOffset = -1f;
            rig.Render(96, 64);
            AssertExactZeros(rig.Scene, "the later render");
            rig.Frame(96, 64);
            AssertBias(rig.Scene, -Math.Log2(1.5) - 1.0);
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        public void ANonFiniteOffsetNeverReachesTheFrameBlock(float offset)
        {
            using HeadlessSceneRig rig = TemporalRig(TemporalUpscale.Performance);
            rig.Scene.Post.Temporal.MipBiasOffset = offset;
            rig.Frame(96, 64);
            var (bias, gradScale) = Lod(rig.Scene);
            Assert.Equal(-1f, bias);
            Assert.Equal(-0.5f, gradScale);
        }

        [Theory]
        [InlineData(-9f, -3.0)]
        [InlineData(5f, 0.0)]
        public void TheOffsetIsClampedInTheFrameBlock(float offset, double bias)
        {
            using HeadlessSceneRig rig = TemporalRig(TemporalUpscale.Performance);
            rig.Scene.Post.Temporal.MipBiasOffset = offset;
            rig.Frame(96, 64);
            AssertBias(rig.Scene, bias);
        }
    }
}
