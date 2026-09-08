using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>Headless tests for the <see cref="GpuCapabilities"/> value type (ctor / property plumbing).</summary>
    public sealed class GpuCapabilitiesTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void Ctor_StoresFlags(bool clipY, bool depth01)
        {
            var caps = new GpuCapabilities(clipY, depth01);
            Assert.Equal(clipY, caps.ClipSpaceYInverted);
            Assert.Equal(depth01, caps.DepthRangeZeroToOne);
        }

        [Fact]
        public void Default_IsAllFalse()
        {
            var caps = default(GpuCapabilities);
            Assert.False(caps.ClipSpaceYInverted);
            Assert.False(caps.DepthRangeZeroToOne);
            Assert.Equal(GpuSampleCounts.One, caps.SupportedMsaaSampleCounts);
            Assert.Equal(1, caps.MaxMsaaSampleCount);
            Assert.True(caps.SupportsMsaaSampleCount(1));
            Assert.Equal(1, caps.HighestSupportedMsaaSampleCountAtMost(32));
        }

        [Fact]
        public void LegacyConstructorTreatsCountsThroughTheMaximumAsSupported()
        {
            var caps = new GpuCapabilities(false, true, maxMsaaSampleCount: 4);

            Assert.Equal(4, caps.MaxMsaaSampleCount);
            Assert.True(caps.SupportsMsaaSampleCount(1));
            Assert.True(caps.SupportsMsaaSampleCount(2));
            Assert.True(caps.SupportsMsaaSampleCount(4));
            Assert.False(caps.SupportsMsaaSampleCount(8));
        }

        [Fact]
        public void ExplicitSupportedCountsCanCarryAHole()
        {
            GpuCapabilities caps = GpuCapabilities.FromSupportedMsaaSampleCounts(
                false, true, GpuSampleCounts.One | GpuSampleCounts.Four);

            Assert.Equal(GpuSampleCounts.One | GpuSampleCounts.Four, caps.SupportedMsaaSampleCounts);
            Assert.Equal(4, caps.MaxMsaaSampleCount);
            Assert.False(caps.SupportsMsaaSampleCount(2));
            Assert.Equal(1, caps.HighestSupportedMsaaSampleCountAtMost(2));
            Assert.Equal(4, caps.HighestSupportedMsaaSampleCountAtMost(8));
        }
    }
}
