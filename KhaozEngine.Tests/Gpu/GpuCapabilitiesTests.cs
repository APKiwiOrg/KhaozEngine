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
        }
    }
}
