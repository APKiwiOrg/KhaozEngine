using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE SPLIT'S SECOND PASS AT THE DISPLAY'S OWN SIZE, PINNED
    /// (<see cref="ShaderSources.TemporalAccumulateAtDisplaySizeFrag"/>). It is the upscaling program with one switch
    /// after the precision header, validates, and is what the split records on a frame whose display is no larger than
    /// its internal size, the test the shader makes before a pixel may take the display-sized reconstruction.
    /// Device-free. That it writes the same history as the upscaling program there is held on a GPU by the temporal
    /// facts' printed lines and by <c>TemporalEntryIdentityGpuTests</c> at Native.
    /// </summary>
    public sealed class TemporalAccumulateAtDisplaySizeTests
    {
        [Theory]
        [InlineData("Full")]
        [InlineData("Half")]
        public void TheProgramIsTheUpscalingOneWithTheSwitchAfterThePrecisionHeader(string precisionName)
        {
            var precision = System.Enum.Parse<TemporalResolvePrecision>(precisionName);
            string header = precision == TemporalResolvePrecision.Half
                ? ShaderSources.TemporalHalfPrecisionGlsl : ShaderSources.TemporalFullPrecisionGlsl;
            string upscaling = ShaderSources.TemporalAccumulateFragment(precision, upscales: true);
            string atDisplaySize = ShaderSources.TemporalAccumulateFragment(precision, upscales: false);
            Assert.Equal(ShaderSources.TemporalAccumulateFragment(precision), upscaling);
            Assert.Equal(upscaling.Replace(header, header + ShaderSources.TemporalAtDisplaySizeGlsl), atDisplaySize);
        }

        [Fact]
        public void TheFullProgramValidates() => ShaderValidation.ValidatePair(ShaderSources.FullscreenVert,
            ShaderSources.TemporalAccumulateAtDisplaySizeFrag, "TemporalAccumulateAtDisplaySize");

        [Theory]
        [InlineData(2560, 1440, 2560, 1440, false)]
        [InlineData(3456, 2234, 3342, 2160, true)]   // Native under the default render cap
        [InlineData(2560, 1440, 1707, 960, true)]    // Quality
        [InlineData(1280, 720, 1280, 720, false)]
        public void AFrameUpscalesWhereTheShadersRatioIsAboveOne(int dw, int dh, int iw, int ih, bool upscales)
        {
            var view = new TemporalViewInput(Matrix4x4.Identity, Matrix4x4.Identity);
            TemporalResolveUniforms u = TemporalResolveMath.BuildUniforms(view, null, Vector2.Zero, iw, ih, dw, dh,
                historyValid: false);
            Assert.Equal(upscales, TemporalSplitResolve.Upscales(u));
            Assert.Equal(upscales, u.Jitter.Z > 1f);
        }

        [Theory]
        [InlineData(GpuBackendKind.MetalNative)]
        [InlineData(GpuBackendKind.VulkanNative)]
        [InlineData(GpuBackendKind.Direct3D11Native)]
        public void TheSplitRecordsTheProgramForTheFramesSize(GpuBackendKind backend)
        {
            using var rig = new HeadlessSceneRig(backend);
            rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            rig.Scene.TemporalResolveEntryForTests = TemporalResolveEntry.Split;
            TemporalResolvePrecision precision = TemporalResolvePrecisionPolicy.For(backend);
            string upscaling = ShaderSources.TemporalAccumulateFragment(precision, upscales: true);
            string atDisplaySize = ShaderSources.TemporalAccumulateFragment(precision, upscales: false);

            rig.Scene.Post.Temporal.Upscale = TemporalUpscale.Native;
            rig.Frame();
            TemporalResolveRenderer resolve = rig.Scene.TemporalResolveRendererForTests!;
            Assert.False(TemporalSplitResolve.Upscales(resolve.LastUniforms));
            Assert.Equal(atDisplaySize, resolve.LastEntryFragmentsForTests()[1]);
            Assert.True(resolve.SplitResolveForTests!.AtDisplaySizeBuiltForTests);

            rig.Scene.Post.Temporal.Upscale = TemporalUpscale.Performance;
            rig.Frame();
            Assert.True(TemporalSplitResolve.Upscales(resolve.LastUniforms));
            Assert.Equal(upscaling, resolve.LastEntryFragmentsForTests()[1]);

            string[] requested = rig.Factory.GraphicsPipelines.Select(p => p.FragmentGlsl).ToArray();
            Assert.Single(requested, f => f == atDisplaySize);
            Assert.Single(requested, f => f == upscaling);
        }

        [Fact]
        public void AnUpscalingSceneNeverBuildsTheProgramForTheDisplaysSize()
        {
            using var rig = new HeadlessSceneRig(GpuBackendKind.MetalNative);
            rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            rig.Scene.Post.Temporal.Upscale = TemporalUpscale.Quality;
            rig.Scene.TemporalResolveEntryForTests = TemporalResolveEntry.Split;
            for (int i = 0; i < 3; i++) rig.Frame();
            Assert.False(rig.Scene.TemporalResolveRendererForTests!.SplitResolveForTests!.AtDisplaySizeBuiltForTests);
            Assert.DoesNotContain(rig.Factory.GraphicsPipelines, p => p.FragmentGlsl
                == ShaderSources.TemporalAccumulateFragment(TemporalResolvePrecision.Half, upscales: false));
        }
    }
}
