using KhaozEngine.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// <see cref="GpuCapabilities.MaxColorAttachments"/>: what a backend reports, and the least any backend guarantees
    /// when a capability is built without it.
    /// </summary>
    public sealed class GpuColorAttachmentsTests(ITestOutputHelper output)
    {
        [Fact]
        public void A_capability_built_without_the_limit_reports_the_least_any_backend_guarantees()
        {
            var built = new GpuCapabilities(clipSpaceYInverted: false, depthRangeZeroToOne: true);
            Assert.Equal(GpuCapabilities.MinimumColorAttachments, built.MaxColorAttachments);
            Assert.Equal(4, GpuCapabilities.MinimumColorAttachments);
            Assert.Equal(8, (built with { MaxColorAttachments = 8 }).MaxColorAttachments);
            Assert.Equal(4, (built with { MaxColorAttachments = 0 }).MaxColorAttachments);
        }

        [Fact]
        public void A_spy_that_hides_the_fences_keeps_the_limit()
        {
            using var spy = new SpyGpuDevice(new FakeGpuDevice(maxColorAttachments: 6), suppressFences: true);
            Assert.False(spy.Capabilities.SupportsCompletionFences);
            Assert.Equal(6, spy.Capabilities.MaxColorAttachments);
        }

        [GpuFact]
        public void A_live_device_reports_its_colour_attachment_limit()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            int max = ctx.Capabilities.MaxColorAttachments;
            output.WriteLine($"{ctx.Backend} on {ctx.Capabilities.DeviceName}: {max} colour attachments");
            Assert.True(max >= GpuCapabilities.MinimumColorAttachments, $"{max} colour attachments");
            if (ctx.Backend.IsDirect3D11() || ctx.Backend.IsMetal()) Assert.Equal(8, max);
        }
    }
}
