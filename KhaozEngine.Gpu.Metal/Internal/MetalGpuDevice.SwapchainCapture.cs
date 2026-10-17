using System;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE SWAPCHAIN CAPTURE (<see cref="IGpuSwapchainCapture"/>). A drawable vended with <c>framebufferOnly</c>
    /// true cannot be a blit source, so the request is queued as a <c>framebufferOnly</c> change and applied by
    /// <see cref="MetalPresentBoundary"/> before it takes the next drawable. Copyability is read off each acquired
    /// texture's own <c>-isFramebufferOnly</c>, so the drawable held when the request arrives, or a pooled one the
    /// layer recycles from before the write, is not copyable.
    /// <para>
    /// On a HEADLESS device the request is a plain backing value and nothing is ever copyable, because there is no
    /// swapchain.
    /// </para>
    /// </summary>
    internal sealed partial class MetalGpuDevice : IGpuSwapchainCapture
    {
        bool _swapchainCopySourceRequested;

        /// <inheritdoc/>
        public bool SwapchainCopySourceRequested
        {
            get => _swapchainCopySourceRequested;
            set
            {
                _swapchainCopySourceRequested = value;
                _present?.Pending.QueueFramebufferOnly(!value);
            }
        }

        /// <inheritdoc/>
        public bool SwapchainCopyable => _present?.CurrentDrawableCopyable ?? false;

        /// <inheritdoc/>
        public void CopySwapchainColor(IGpuCommandList commands, IGpuTexture destination)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(commands);
            ArgumentNullException.ThrowIfNull(destination);

            MetalPresentBoundary present = _present is { CurrentDrawableCopyable: true } boundary
                ? boundary
                : throw new InvalidOperationException(
                    "CopySwapchainColor was called while the swapchain is not copyable: the device has no "
                    + "swapchain, the held drawable's texture reports framebufferOnly because it was made before the "
                    + "copy-source request applied, or the layer had no drawable and the frame is on the orphan "
                    + "target. Check "
                    + "SwapchainCopyable first.");

            SwapchainCopyCheck.RequireDestination(present.Framebuffer, destination);
            RequireList(commands, this).CopyFromDrawable(present.Framebuffer.Attachment.Texture, destination);
        }
    }
}
