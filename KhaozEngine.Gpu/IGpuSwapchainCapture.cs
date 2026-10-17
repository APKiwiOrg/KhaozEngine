namespace KhaozEngine.Gpu
{
    /// <summary>
    /// An optional device capability: a whole copy of the current swapchain colour image into a texture, recorded
    /// like any other copy. The frame capture path uses it to take the image a frame rendered before it is
    /// presented. Test for it with a type check on the device. The three native devices implement it. Fakes and
    /// headless devices need not.
    /// </summary>
    public interface IGpuSwapchainCapture
    {
        /// <summary>
        /// Ask for copy-source usage on the swapchain colour images. Applied at the next present boundary, so the
        /// first frame after a change may still render into an image of the old usage. A backend whose swapchain
        /// images are always copyable stores the value and does nothing else.
        /// </summary>
        bool SwapchainCopySourceRequested { get; set; }

        /// <summary>
        /// True when this device can ever produce a copyable swapchain image for its surface. False means
        /// <see cref="SwapchainCopyable"/> never becomes true whatever is requested, such as on a Vulkan surface
        /// whose <c>supportedUsageFlags</c> lack <c>TRANSFER_SRC</c>, so a caller should not wait for a copyable
        /// frame. Direct3D 11 and Metal always answer true. True does not mean the current frame is copyable: that
        /// is <see cref="SwapchainCopyable"/>, which stays false for transient frames such as the first after a
        /// request, a minimised window or a recreation.
        /// </summary>
        bool SwapchainCopySupported { get; }

        /// <summary>True when the image the current frame renders into can be copied out.</summary>
        bool SwapchainCopyable { get; }

        /// <summary>
        /// Record, into an open recording of <paramref name="commands"/>, a whole copy of the current swapchain
        /// colour image into <paramref name="destination"/>.
        /// </summary>
        /// <param name="commands">A recording of this device's commands, between <c>Begin</c> and <c>End</c>,
        /// that has bound <see cref="IGpuDevice.SwapchainFramebuffer"/> in this recording: the frame's own
        /// list.</param>
        /// <param name="destination">A texture of this device with the same width and height as
        /// <see cref="IGpuDevice.SwapchainFramebuffer"/>, the swapchain's colour format, one mip and one
        /// sample.</param>
        /// <exception cref="System.InvalidOperationException"><see cref="SwapchainCopyable"/> is false, or
        /// <paramref name="commands"/> has not bound the swapchain framebuffer in this recording. Nothing is
        /// recorded.</exception>
        /// <exception cref="System.ArgumentException"><paramref name="destination"/> does not match the swapchain
        /// image's shape. Nothing is recorded.</exception>
        void CopySwapchainColor(IGpuCommandList commands, IGpuTexture destination);
    }
}
