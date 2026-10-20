using System;
using KhaozEngine.Gpu.Internal;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE SWAPCHAIN COPY: a whole copy of the bound swapchain attachment into a texture, for
    /// <see cref="IGpuSwapchainCapture.CopySwapchainColor"/>. The source is a <see cref="VulkanAttachment"/> rather
    /// than a <see cref="VulkanTexture"/>, because the image belongs to the presentation engine, so the public
    /// <c>CopyTexture</c> cannot take it. Everything else is the texture copy path's own steps.
    /// <para>
    /// <b>THE PASS ENDS FIRST, AND A PENDING CLEAR IS APPLIED BY IT.</b>
    /// <see cref="EndRenderingBeforeIllegalCommand"/> runs the clear-only begin and end a pass with no draw owes,
    /// so a frame that only cleared copies the cleared image.
    /// </para>
    /// <para>
    /// <b>THE TRACKER MOVES THE IMAGE AND <c>End</c> PUTS IT BACK.</b> The attachment is tracked over the range a
    /// pass tracks it over, so after a draw or a clear-only pass it leaves <c>COLOR_ATTACHMENT_OPTIMAL</c> for
    /// <c>TRANSFER_SRC_OPTIMAL</c> with its contents kept, and <c>End</c> restores it to its
    /// <c>PRESENT_SRC_KHR</c> resting layout inside the submit the present waits on.
    /// </para>
    /// <para>
    /// <b>A LIST THAT NEVER BOUND THE SWAPCHAIN IS REFUSED.</b> It would find the image at rest in
    /// <c>PRESENT_SRC_KHR</c>, and a transition out of that discards (V-F8). Its submit would also not carry the
    /// frame's acquire semaphore, because <see cref="BoundSwapchainFramebuffer"/> routes the pair, so the copy
    /// would not be ordered after the acquire. The same flag decides both.
    /// </para>
    /// </summary>
    internal sealed partial class VulkanCommandList
    {
        const string SwapchainCopy = "Copying the swapchain image";

        /// <summary>
        /// Record a whole copy of <paramref name="source"/>, the swapchain framebuffer's current attachment, into
        /// <paramref name="dst"/>. The caller has already checked that the shapes match.
        /// </summary>
        /// <exception cref="InvalidOperationException">This recording never bound the swapchain framebuffer.
        /// Nothing is recorded.</exception>
        internal void CopyFromSwapchain(in VulkanAttachment source, IGpuTexture dst)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SwapchainCopyCheck.RequireSwapchainBound(_boundSwapchain);

            VulkanTexture destination = VulkanTexture.Require(dst, "a native Vulkan swapchain copy");
            IVulkanTransferSink sink = RequireTransfers(SwapchainCopy);

            ulong buffer = CurrentBuffer;
            EndRenderingBeforeIllegalCommand();

            _layouts?.TransitionTo(buffer, VulkanTrackedImage.ForAttachment(source), ImageLayout.TransferSrcOptimal);

            if (destination.IsStaging)
            {
                sink.MemoryBarrier(buffer, toTransfer: true);
                sink.CopyImageToBuffer(buffer, source.Image, destination.StagingBuffer,
                    VulkanTransferPlan.BufferImageRegion(
                        VulkanStagingLayout.CopyRegion(destination.StagingShape, 0, 0, 0, 0, destination.Width,
                            destination.Height),
                        0, 0, depthStencil: false));
            }
            else
            {
                _layouts?.TransitionTo(buffer, VulkanTransferPlan.Tracked(destination, 0, 1, 0, 1),
                    ImageLayout.TransferDstOptimal);
                sink.CopyImage(buffer, source.Image, destination.Image,
                    VulkanTransferPlan.ImageRegion(0, 0, 0, 0, destination.Width, destination.Height,
                        depthStencil: false));
            }

            FinishTransfer(buffer, destination, SwapchainCopy);
        }
    }
}
