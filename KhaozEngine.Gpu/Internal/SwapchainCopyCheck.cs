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

        /// <summary>The refusal every backend gives a swapchain copy recorded in a list that never bound the
        /// swapchain framebuffer.</summary>
        internal const string NotBoundMessage =
            "CopySwapchainColor needs a command list that bound the swapchain framebuffer in this recording. Record "
            + "the copy in the frame's own list, after it binds SwapchainFramebuffer.";

        /// <summary>
        /// Refuse a swapchain copy unless the list bound the swapchain framebuffer in its current recording. Only
        /// that list is ordered after the image's acquire and holds its rendered contents. Each backend supplies
        /// the fact from its own command list.
        /// </summary>
        /// <exception cref="InvalidOperationException"><paramref name="boundThisRecording"/> is false.</exception>
        internal static void RequireSwapchainBound(bool boundThisRecording)
        {
            if (!boundThisRecording) throw new InvalidOperationException(NotBoundMessage);
        }

        static string Shape(uint width, uint height, GpuPixelFormat format, uint mipLevels, uint sampleCount)
            => string.Create(CultureInfo.InvariantCulture,
                $"{width}x{height} {format}, {mipLevels} mip(s), {sampleCount} sample(s)");
    }
}
