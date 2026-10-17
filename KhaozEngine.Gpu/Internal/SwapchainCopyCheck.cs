using System;
using System.Globalization;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>
    /// The destination rule of <see cref="IGpuSwapchainCapture.CopySwapchainColor"/>, in one place for the three
    /// native backends. A whole copy demands two identical shapes: Direct3D 11's <c>CopyResource</c> and the Metal
    /// and Vulkan whole-image copies all refuse or misbehave on a mismatch, so it is refused here, by name and
    /// identically, before anything is recorded.
    /// </summary>
    internal static class SwapchainCopyCheck
    {
        /// <summary>
        /// Refuse <paramref name="destination"/> unless it has <paramref name="swapchain"/>'s width, height and
        /// colour format, one mip and one sample.
        /// </summary>
        /// <exception cref="ArgumentException">The shapes differ. The message names both.</exception>
        internal static void RequireDestination(IGpuFramebuffer swapchain, IGpuTexture destination)
        {
            ArgumentNullException.ThrowIfNull(swapchain);
            ArgumentNullException.ThrowIfNull(destination);

            GpuPixelFormat[] colour = swapchain.Outputs.Colour;
            if (colour.Length != 1)
            {
                throw new ArgumentException(
                    "A swapchain copy needs a swapchain framebuffer with exactly one colour attachment, and this one "
                    + "has " + colour.Length.ToString(CultureInfo.InvariantCulture) + ".", nameof(swapchain));
            }

            GpuPixelFormat format = colour[0];
            if (destination.Width == swapchain.Width && destination.Height == swapchain.Height
                && destination.Format == format && destination.MipLevels == 1 && destination.SampleCount == 1)
            {
                return;
            }

            throw new ArgumentException(
                "A swapchain copy is a whole copy, so the destination must match the swapchain image exactly. The "
                + "destination is " + Shape(destination.Width, destination.Height, destination.Format,
                    destination.MipLevels, destination.SampleCount)
                + " and the swapchain image is " + Shape(swapchain.Width, swapchain.Height, format, 1, 1) + ".",
                nameof(destination));
        }

        static string Shape(uint width, uint height, GpuPixelFormat format, uint mipLevels, uint sampleCount)
            => string.Create(CultureInfo.InvariantCulture,
                $"{width}x{height} {format}, {mipLevels} mip(s), {sampleCount} sample(s)");
    }
}
