using System;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE SWAPCHAIN CAPTURE (<see cref="IGpuSwapchainCapture"/>). A swapchain image is a copy source only when it
    /// was created with <c>TRANSFER_SRC</c>, and a swapchain's usage is fixed at creation, so a change of request
    /// queues a swapchain recreation through <see cref="VulkanPresentBoundary"/>, applied at the next present
    /// boundary. The policy adds the bit only when the surface's <c>supportedUsageFlags</c> include it.
    /// Copyability is read off the usage the current generation was created with, so the frame that made the
    /// request, a surface that cannot honour it and the orphan target are all uncopyable.
    /// <para>
    /// On a HEADLESS device the request is a plain backing value and nothing is ever copyable, because there is no
    /// swapchain.
    /// </para>
    /// </summary>
    internal sealed unsafe partial class VulkanGpuDevice : IGpuSwapchainCapture
    {
        bool _swapchainCopySourceRequested;

        /// <inheritdoc/>
        public bool SwapchainCopySourceRequested
        {
            get => _present?.CopySourceRequested ?? _swapchainCopySourceRequested;
            set
            {
                _swapchainCopySourceRequested = value;
                if (_present is not null) _present.CopySourceRequested = value;
            }
        }

        /// <inheritdoc/>
        public bool SwapchainCopyable => _present?.CurrentGenerationCopyable ?? false;

        /// <inheritdoc/>
        public void CopySwapchainColor(IGpuCommandList commands, IGpuTexture destination)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(commands);
            ArgumentNullException.ThrowIfNull(destination);

            VulkanSwapchainFramebuffer framebuffer =
                _present is { CurrentGenerationCopyable: true, Framebuffer: { } bound }
                    ? bound
                    : throw new InvalidOperationException(
                        "CopySwapchainColor was called while the swapchain is not copyable: the device has no "
                        + "swapchain, the current generation was created before the copy-source request applied "
                        + "or on a surface without TRANSFER_SRC support, or the frame is on the orphan target. "
                        + "Check SwapchainCopyable first.");

            SwapchainCopyCheck.RequireDestination(framebuffer, destination);

            if (commands is not VulkanCommandList list)
            {
                throw new ArgumentException(
                    "A command list created by another GPU backend was handed to the native Vulkan backend's "
                    + $"CopySwapchainColor. A {commands.GetType().Name} holds no VkCommandBuffer to record the "
                    + "copy into. Create command lists from the device you copy with.", nameof(commands));
            }

            list.CopyFromSwapchain(framebuffer.Attachment, destination);
        }
    }
}
