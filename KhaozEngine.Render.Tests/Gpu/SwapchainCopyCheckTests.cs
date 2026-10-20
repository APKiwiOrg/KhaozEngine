using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The destination rule every <see cref="IGpuSwapchainCapture.CopySwapchainColor"/> shares: same size as the
    /// swapchain framebuffer, its colour format, one mip and one sample. Checked once at the seam so the three
    /// native backends refuse a mismatch with one wording.
    /// </summary>
    public sealed class SwapchainCopyCheckTests
    {
        static readonly IGpuFramebuffer Swapchain =
            new FakeFramebuffer(new GpuOutputDescription(null, GpuPixelFormat.B8G8R8A8UNorm), 1280, 720);

        [Fact]
        public void A_destination_matching_the_swapchain_is_accepted()
        {
            var destination = new FakeTexture(1280, 720, 1, 1, GpuPixelFormat.B8G8R8A8UNorm);

            SwapchainCopyCheck.RequireDestination(Swapchain, destination);
        }

        [Fact]
        public void A_destination_of_another_size_is_refused_naming_both_sizes()
        {
            var destination = new FakeTexture(640, 360, 1, 1, GpuPixelFormat.B8G8R8A8UNorm);

            var refusal = Assert.Throws<ArgumentException>(
                () => SwapchainCopyCheck.RequireDestination(Swapchain, destination));

            Assert.Equal("destination", refusal.ParamName);
            Assert.Contains("640x360", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("1280x720", refusal.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_destination_of_another_format_is_refused_naming_both_formats()
        {
            var destination = new FakeTexture(1280, 720, 1, 1, GpuPixelFormat.R8G8B8A8UNorm);

            var refusal = Assert.Throws<ArgumentException>(
                () => SwapchainCopyCheck.RequireDestination(Swapchain, destination));

            Assert.Equal("destination", refusal.ParamName);
            Assert.Contains(nameof(GpuPixelFormat.R8G8B8A8UNorm), refusal.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(GpuPixelFormat.B8G8R8A8UNorm), refusal.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_mipped_or_multisampled_destination_is_refused()
        {
            var mipped = new FakeTexture(1280, 720, 2, 1, GpuPixelFormat.B8G8R8A8UNorm);
            var multisampled = new FakeTexture(1280, 720, 1, 4, GpuPixelFormat.B8G8R8A8UNorm);

            Assert.Equal("destination", Assert.Throws<ArgumentException>(
                () => SwapchainCopyCheck.RequireDestination(Swapchain, mipped)).ParamName);
            Assert.Equal("destination", Assert.Throws<ArgumentException>(
                () => SwapchainCopyCheck.RequireDestination(Swapchain, multisampled)).ParamName);
        }
    }
}
