using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE RESOLVE'S ENTRY POINT PER BACKEND AND PRESET, PINNED (<see cref="TemporalResolvePolicy"/>). The split on
    /// Metal and Vulkan at the upscaling presets, the fused pass on Direct3D 11 and at Native everywhere, from the
    /// cost measurement's table on the policy's summary. Device-free.
    /// </summary>
    public sealed class TemporalResolvePolicyTests
    {
        [Theory]
        [InlineData(GpuBackendKind.MetalNative, TemporalUpscale.Native, "Fused")]
        [InlineData(GpuBackendKind.MetalNative, TemporalUpscale.Quality, "Split")]
        [InlineData(GpuBackendKind.MetalNative, TemporalUpscale.UltraPerformance, "Split")]
        [InlineData(GpuBackendKind.VulkanNative, TemporalUpscale.Native, "Fused")]
        [InlineData(GpuBackendKind.VulkanNative, TemporalUpscale.Quality, "Split")]
        [InlineData(GpuBackendKind.VulkanNative, TemporalUpscale.Balanced, "Split")]
        [InlineData(GpuBackendKind.VulkanNative, TemporalUpscale.Performance, "Split")]
        [InlineData(GpuBackendKind.Direct3D11Native, TemporalUpscale.Native, "Fused")]
        [InlineData(GpuBackendKind.Direct3D11Native, TemporalUpscale.Quality, "Fused")]
        [InlineData(GpuBackendKind.Direct3D11Native, TemporalUpscale.UltraPerformance, "Fused")]
        public void TheMeasuredPickFollowsTheTable(GpuBackendKind backend, TemporalUpscale preset,
            string expected)
            => Assert.Equal(expected, TemporalResolvePolicy.Measured(backend, preset).ToString());

        [Theory]
        [InlineData("fused", "Fused")]
        [InlineData("SPLIT", "Split")]
        [InlineData("Split", "Split")]
        public void TheOverrideNamesAnEntryPointInAnyCase(string value, string expected)
            => Assert.Equal(expected, TemporalResolvePolicy.Parse(value)?.ToString());

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("policy")]
        [InlineData("both")]
        public void AnythingElseLeavesThePolicyToChoose(string? value)
            => Assert.Null(TemporalResolvePolicy.Parse(value));
    }
}
