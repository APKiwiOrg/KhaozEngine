using System;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>The scene writes the preset's mip bias into the frame block under temporal anti-aliasing, and
    /// exact zeros under every other mode and on a later render inside a resolving frame.</summary>
    public sealed class TemporalMaterialLodGpuTests
    {
        static (float Bias, float GradScale) Lod(Scene3D scene)
        {
            ReadOnlySpan<byte> frameBlock = scene.FrameImageForTests;
            return (BitConverter.ToSingle(frameBlock.Slice(120, 4)), BitConverter.ToSingle(frameBlock.Slice(124, 4)));
        }

        static TemporalFixture NewFixture(AntiAliasing aa, TemporalUpscale preset) => new(320, 180, s =>
        {
            s.Post.Quality.AntiAliasing = aa;
            s.Post.Temporal.Upscale = preset;
        });

        [GpuFact]
        public void PerformanceBiasesByMinusOneAndAHalf()
        {
            using TemporalFixture fx = NewFixture(AntiAliasing.Temporal, TemporalUpscale.Performance);
            fx.Frame((_, _) => { });
            var (bias, grad) = Lod(fx.Scene);
            Assert.Equal(-1.5f, bias, 3);
            Assert.Equal(MathF.Pow(2f, -1.5f) - 1f, grad, 4);
        }

        [GpuFact]
        public void FxaaWritesExactZeros()
        {
            using TemporalFixture fx = NewFixture(AntiAliasing.Fxaa, TemporalUpscale.Performance);
            fx.Frame((_, _) => { });
            var (bias, grad) = Lod(fx.Scene);
            Assert.Equal(0, BitConverter.SingleToInt32Bits(bias));
            Assert.Equal(0, BitConverter.SingleToInt32Bits(grad));
        }

        [GpuFact]
        public void ALaterRenderInsideAResolvingFrameWritesExactZeros()
        {
            using TemporalFixture fx = NewFixture(AntiAliasing.Temporal, TemporalUpscale.Performance);
            fx.Frame((_, _) => { });
            Assert.Equal(-1.5f, Lod(fx.Scene).Bias, 3);
            fx.RenderSecond(160, 90);
            var (bias, grad) = Lod(fx.Scene);
            Assert.Equal(0, BitConverter.SingleToInt32Bits(bias));
            Assert.Equal(0, BitConverter.SingleToInt32Bits(grad));
        }
    }
}
