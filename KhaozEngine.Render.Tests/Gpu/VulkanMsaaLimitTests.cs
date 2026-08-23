using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// <c>GpuCapabilities.MaxMsaaSampleCount</c> AS THE INCUMBENT COMPUTES IT (V-C5), device-free. Work-breakdown
    /// row 15 (https://github.com/APKiwiOrg/KhaozEngine/issues/525).
    ///
    /// <para><b>WHAT THIS PINS IS THE SHAPE OF THE QUESTION, NOT AN ANSWER.</b> Row 18's parity test
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/528) asserts the two backends agree on a real device, and
    /// that assertion is only worth anything if both ask the SAME question. Both design drafts invented a formula,
    /// the two formulas differ, at most one of them could have equalled the incumbent's, and both then asserted
    /// equality as a test. So what is checked here is that the fold covers the engine's three MRT targets, that
    /// the reduction is the incumbent's own ladder, and that the depth flag reaches the USAGE and not the format
    /// mapping.</para>
    /// </summary>
    public sealed class VulkanMsaaLimitTests
    {
        /// <summary>
        /// THE FOLD IS THE MINIMUM OVER THE ENGINE'S THREE MRT TARGETS, because every attachment of a
        /// multi-target pass has to support the count. A device generous about colour and stingy about depth
        /// answers the depth number.
        /// </summary>
        [Fact]
        public void TheLimit_IsTheMinimumOverTheThreeTargets()
        {
            var answers = new Dictionary<GpuPixelFormat, SampleCountFlags>
            {
                [GpuPixelFormat.R8G8B8A8UNorm] = All(8),
                [GpuPixelFormat.R32Float] = All(8),
                [GpuPixelFormat.D32FloatS8UInt] = All(2),
            };

            Assert.Equal(2, VulkanMsaaLimit.MinOverTheEngineTargets((format, _) => answers[format]));

            answers[GpuPixelFormat.R32Float] = All(1);
            Assert.Equal(1, VulkanMsaaLimit.MinOverTheEngineTargets((format, _) => answers[format]));
        }

        /// <summary>
        /// THE THREE FORMATS ARE THE ENGINE'S OWN MRT AND THE DEPTH FLAG IS SET ON EXACTLY ONE OF THEM, which is
        /// what <c>VeldridMap.MaxMsaaSampleCount</c> (deleted in 18.0.0) passed: colour, linear depth as a COLOUR
        /// target, and the combined depth-stencil target as a depth one.
        /// </summary>
        [Fact]
        public void TheThreeTargets_AreTheEnginesOwnAndOnlyTheLastIsQueriedAsDepth()
        {
            var asked = new List<(GpuPixelFormat Format, bool DepthAttachment)>();
            VulkanMsaaLimit.MinOverTheEngineTargets((format, depth) =>
            {
                asked.Add((format, depth));
                return All(4);
            });

            Assert.Equal(
                [(GpuPixelFormat.R8G8B8A8UNorm, false), (GpuPixelFormat.R32Float, false),
                    (GpuPixelFormat.D32FloatS8UInt, true)],
                asked);
        }

        /// <summary>
        /// THE REDUCTION IS THE INCUMBENT'S LADDER: the highest recognised bit, and 1 rather than 0 for a mask
        /// with none. A driver that failed the query leaves a zeroed structure behind there, so the floor is what
        /// the incumbent silently answered and what this says out loud.
        /// </summary>
        [Fact]
        public void TheReduction_IsTheHighestBitAndOneForNone()
        {
            Assert.Equal(32, VulkanMsaaLimit.Reduce(All(32)));
            Assert.Equal(16, VulkanMsaaLimit.Reduce(All(16)));
            Assert.Equal(8, VulkanMsaaLimit.Reduce(All(8)));
            Assert.Equal(4, VulkanMsaaLimit.Reduce(All(4)));
            Assert.Equal(2, VulkanMsaaLimit.Reduce(All(2)));
            Assert.Equal(1, VulkanMsaaLimit.Reduce(All(1)));
            Assert.Equal(1, VulkanMsaaLimit.Reduce(SampleCountFlags.None));

            // A SPARSE MASK ANSWERS ITS HIGHEST BIT rather than the lowest, which is the direction that matters:
            // reporting less than the device supports would silently cap every quality setting.
            Assert.Equal(8, VulkanMsaaLimit.Reduce(SampleCountFlags.Count1Bit | SampleCountFlags.Count8Bit));
        }

        /// <summary>
        /// THE CITATION NAMES BOTH MEMBERS RATHER THAN TWO LINE NUMBERS (V-I6), because phase 2's own cited lines
        /// went stale and because a reader comparing the two sources needs to know which two functions to open.
        /// </summary>
        [Fact]
        public void TheCitation_NamesTheTwoMembersItReproduces()
        {
            Assert.Contains("GetSampleCountLimit", VulkanMsaaLimit.Citation, StringComparison.Ordinal);
            Assert.Contains("VeldridMap.MaxMsaaSampleCount", VulkanMsaaLimit.Citation, StringComparison.Ordinal);
            Assert.DoesNotContain("line ", VulkanMsaaLimit.Citation, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// AND THE FORMAT MAPPING IGNORES THE DEPTH FLAG FOR ALL THREE, which is the clause that looks like a bug
        /// and is the contract. <c>GetSampleCountLimit</c> hands its own <c>depthFormat</c> argument to the usage
        /// bits alone, so the linear-depth target is queried as <c>R32_SFLOAT</c>, and the combined format carries
        /// its depth spelling whatever the flag says.
        /// </summary>
        [Fact]
        public void TheFormatMapping_IsTheColourSpellingEvenForTheDepthQuery()
        {
            Assert.Equal(Format.R8G8B8A8Unorm,
                VulkanFormats.ToVkFormat(GpuPixelFormat.R8G8B8A8UNorm, depthStencil: false));
            Assert.Equal(Format.R32Sfloat,
                VulkanFormats.ToVkFormat(GpuPixelFormat.R32Float, depthStencil: false));
            Assert.Equal(Format.D32SfloatS8Uint,
                VulkanFormats.ToVkFormat(GpuPixelFormat.D32FloatS8UInt, depthStencil: false));
        }

        // Every bit up to and including `highest`, which is what a device reports for a format it fully supports.
        static SampleCountFlags All(int highest)
        {
            SampleCountFlags flags = SampleCountFlags.None;
            foreach (int count in new[] { 1, 2, 4, 8, 16, 32 }.Where(c => c <= highest))
            {
                flags |= VulkanFormats.ToSampleCount((uint)count);
            }

            return flags;
        }
    }
}
