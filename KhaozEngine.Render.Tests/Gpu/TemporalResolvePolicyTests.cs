using System;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE RESOLVE'S ENTRY POINT, PINNED (<see cref="TemporalResolvePolicy"/>). The split on every backend at every
    /// size, from the cost measurement's table on the policy's summary, and the fused pass only where a device allows
    /// too few colour attachments for the split or the override forces it. Device-free.
    /// </summary>
    public sealed class TemporalResolvePolicyTests
    {
        [Fact]
        public void TheMeasuredPickIsTheSplit()
        {
            Assert.Equal(TemporalResolveEntry.Split, TemporalResolvePolicy.Measured);
            Assert.Equal(TemporalResolvePolicy.Forced ?? TemporalResolveEntry.Split, TemporalResolvePolicy.Choose());
        }

        [Theory]
        [InlineData("Split", 6, "Fused")]
        [InlineData("Split", 7, "Split")]
        [InlineData("Split", 8, "Split")]
        [InlineData("Fused", 1, "Fused")]
        public void TheSplitFallsBackToFusedBelowItsFirstPassAttachments(string entry, int maxColorAttachments,
            string expected)
        {
            Assert.Equal(7, TemporalSplitFormats.FirstPassAttachments);
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
