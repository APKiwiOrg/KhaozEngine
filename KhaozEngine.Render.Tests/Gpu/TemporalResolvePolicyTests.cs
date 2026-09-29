using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE RESOLVE'S ENTRY POINT PER BACKEND, PINNED (<see cref="TemporalResolvePolicy"/>). The split wherever the
    /// internal size is below the display's on either axis, and at the display's own size on Metal and Vulkan, from the
    /// cost measurement's table on the policy's summary. Device-free.
    /// </summary>
    public sealed class TemporalResolvePolicyTests
    {
        [Theory]
        [InlineData(GpuBackendKind.MetalNative, false, "Split")]
        [InlineData(GpuBackendKind.MetalNative, true, "Split")]
        [InlineData(GpuBackendKind.VulkanNative, false, "Split")]
        [InlineData(GpuBackendKind.VulkanNative, true, "Split")]
        [InlineData(GpuBackendKind.Direct3D11Native, false, "Fused")]
        [InlineData(GpuBackendKind.Direct3D11Native, true, "Split")]
        public void TheMeasuredPickFollowsTheTable(GpuBackendKind backend, bool upscales, string expected)
            => Assert.Equal(expected, TemporalResolvePolicy.Measured(backend, upscales).ToString());

        [Theory]
        [InlineData(1706, 960, 2560, 1440, true)]
        [InlineData(2560, 1439, 2560, 1440, true)]
        [InlineData(2559, 1440, 2560, 1440, true)]
        [InlineData(2560, 1440, 2560, 1440, false)]
        public void OnlyAnInternalSizeBelowTheDisplayUpscales(int iw, int ih, int dw, int dh, bool expected)
            => Assert.Equal(expected, TemporalResolvePolicy.Upscales(iw, ih, dw, dh));

        [Theory]
        [InlineData("Split", 4, "Fused")]
        [InlineData("Split", 5, "Split")]
        [InlineData("Split", 8, "Split")]
        [InlineData("Fused", 1, "Fused")]
        public void TheSplitFallsBackToFusedBelowItsFirstPassAttachments(string entry, int maxColorAttachments,
            string expected)
        {
            Assert.Equal(5, TemporalSplitFormats.FirstPassAttachments);
            Assert.Equal(expected, TemporalResolvePolicy.Supported(
                Enum.Parse<TemporalResolveEntry>(entry), maxColorAttachments).ToString());
        }

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
