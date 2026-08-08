using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE SWAPCHAIN SURFACE OF THE NATIVE DEVICE: the stable framebuffer identity, vsync, the queued resize,
    /// and <see cref="Present"/> as the frame boundary. Work-breakdown row 17
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/527), split from the seam surface the same way the
    /// creation and submission halves are, because a device that must stay under the file-size cap has room for
    /// none of the three. The machinery itself lives in <see cref="VulkanPresentBoundary"/>: this partial is the
    /// seam-facing shell over it, plus the ring rotation and retire drain that run on every frame, windowed or
    /// headless.
    /// </summary>
    internal sealed unsafe partial class VulkanGpuDevice
    {
        /// <inheritdoc/>
        /// <remarks>
        /// THE SAME OBJECT FOR THE WHOLE LIFE OF A WINDOWED DEVICE (V-W5), so it may be cached by anything: a
        /// recreate and an acquire both change what it points at and never its identity. NULL on a headless
        /// device, which is correct rather than unbuilt: the headless path enables no surface extension at all
        /// (V-N6), which is what lets the golden suite run on a machine with no display server.
        /// </remarks>
        public IGpuFramebuffer? SwapchainFramebuffer => _present?.Framebuffer;

        /// <inheritdoc/>
        /// <remarks>
        /// UNLIKE DIRECT3D 11, THIS RECONFIGURES SOMETHING. There, vsync is an argument of the present call, so a
        /// setter only changes an interval. Vulkan cannot change a swapchain's present mode in place, so a change
        /// here QUEUES A RECREATE that the next present boundary applies, exactly as a resize does (V-W6). The
        /// value is settable from any thread and takes no lock, and the recreate lands on the submit thread where
        /// it provably owns the queue.
        /// <para>
        /// On a HEADLESS device it is a plain backing value, which is what the seam asks for: there is no
        /// swapchain to reconfigure.
        /// </para>
        /// </remarks>
        public bool SyncToVerticalBlank
        {
            get => _present?.SyncToVerticalBlank ?? _syncToVerticalBlank;
            set
            {
                _syncToVerticalBlank = value;
                if (_present is not null) _present.SyncToVerticalBlank = value;
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// QUEUED AND APPLIED AT THE NEXT PRESENT BOUNDARY (V-W6), never here. This stores a number and returns:
        /// no lock, no native call, nothing that can block, so a window callback arriving on any thread while the
        /// submit thread is inside <c>vkQueueSubmit</c> is safe. That makes a foreign-thread resize during
        /// recording STRUCTURALLY impossible rather than contractually forbidden, and it matters more on this
        /// backend than on the other one because recreating a swapchain invalidates every attachment a recording
        /// may already have bound.
        /// <para>
        /// A ZERO IN EITHER DIMENSION IS SAFE HERE AND SAFE AT THE APPLY. A minimised window reports (0, 0)
        /// through its framebuffer-resize event on Windows, and the clamp against the surface's own reported
        /// bounds produces an extent the boundary then reads as not creatable, so no <c>vkCreateSwapchainKHR</c>
        /// is ever made at a size the specification forbids
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/81). The frame goes imageless, binds the orphan
        /// target and skips its present until the window comes back.
        /// </para>
        /// <para>
        /// A no-op on a headless device, which has no swapchain to resize. Silent rather than a throw, matching
        /// the incumbent, because teardown order is a consumer's business and a window can report a size change
        /// while it is being destroyed.
        /// </para>
        /// </remarks>
        public void ResizeSwapchain(uint w, uint h) => _present?.QueueResize(w, h);

        /// <inheritdoc/>
        /// <remarks>
        /// THE FRAME BOUNDARY, IN ONE PLACE (V-W4). It presents the frame just submitted if an image is held,
        /// applies any pending recreate at that same boundary, and acquires for the NEXT frame, then rotates the
        /// uniform ring's segment and drains the deferred-disposal list. It never throws and never reports
        /// failure upward: an <c>OUT_OF_DATE</c> surface, a minimised window and a driver that lost the surface
        /// are all handled inside <see cref="VulkanPresentBoundary"/> and the frame loop above is unchanged.
        /// <para>
        /// THE RING AND THE RETIRE LIST ROTATE AFTER THE BOUNDARY HAS RELEASED THE SUBMIT LOCK, which
        /// <see cref="VulkanRingAllocator.BeginFrame"/> refuses a caller by name for: its segment gate can block,
        /// and blocking inside the submit lock would stop every other thread's submit for the length of a GPU
        /// wait.
        /// </para>
        /// <para>
        /// A HEADLESS DEVICE STILL ROTATES BOTH. It has no swapchain and nothing to present, and it does have a
        /// uniform ring and a retire list, so a headless consumer that runs frames gets the same recycling a
        /// windowed one does rather than an ever-growing retire list.
        /// </para>
        /// </remarks>
        public void Present()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _present?.Present();

            // OUTSIDE EVERY LOCK, and in this order. The ring's frame boundary is what recycles a segment the GPU
            // has finished with, and the retire drain is what actually runs the deferred destroys the frame's
            // disposals recorded.
            _rings.BeginFrame();
            DrainRetiredResources();
        }
    }
}
